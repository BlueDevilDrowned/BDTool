using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Activity;
using BDTool.Contracts.Artifacts;

namespace BDTool.Tests.Activity;

/// <summary>FileArtifactStore 的原子写入、哈希、命名与越界防护行为。</summary>
public sealed class FileArtifactStoreTests
{
    [Fact]
    public async Task WriteAsync_RoundTripsContentAndMetadata()
    {
        using var project = new TemporaryProject();
        var store = new FileArtifactStore(project.ProjectPath);
        var bytes = Encoding.UTF8.GetBytes("hello artifact");

        var reference = await store.WriteAsync(
            new ArtifactWriteRequest { FileName = "sample.txt", Content = bytes, ContentType = string.Empty },
            CancellationToken.None);

        Assert.Equal("sample.txt", reference.FileName);
        Assert.Equal("text/plain", reference.ContentType);
        Assert.Equal(bytes.Length, reference.SizeBytes);
        Assert.Equal(ExpectedSha256(bytes), reference.Sha256);
        Assert.StartsWith(".bdtool/artifacts/", reference.RelativePath, StringComparison.Ordinal);
        Assert.StartsWith("art-", reference.ArtifactId, StringComparison.Ordinal);
        Assert.True(File.Exists(project.Resolve(reference.RelativePath)));

        var content = await store.ReadAsync(reference, CancellationToken.None);
        Assert.Equal(bytes, content.Bytes);
        Assert.Equal(reference.Sha256, content.Reference.Sha256);
        Assert.Equal(reference.SizeBytes, content.Reference.SizeBytes);
        Assert.Equal(reference.RelativePath, content.Reference.RelativePath);

        Assert.True(await store.ExistsAsync(reference, CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_PreservesExplicitContentType()
    {
        using var project = new TemporaryProject();
        var store = new FileArtifactStore(project.ProjectPath);

        var reference = await store.WriteAsync(
            new ArtifactWriteRequest
            {
                FileName = "console.log",
                Content = Encoding.UTF8.GetBytes("line"),
                ContentType = "text/x-bdtool-log"
            },
            CancellationToken.None);

        Assert.Equal("text/x-bdtool-log", reference.ContentType);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("a/b.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("name.")]
    [InlineData("   ")]
    public async Task WriteAsync_RejectsUnsafeFileNames(string fileName)
    {
        using var project = new TemporaryProject();
        var store = new FileArtifactStore(project.ProjectPath);

        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(
            new ArtifactWriteRequest { FileName = fileName, Content = new byte[] { 1 } },
            CancellationToken.None));

        Assert.False(Directory.Exists(project.ArtifactsRoot)
            && Directory.GetFiles(project.ArtifactsRoot).Length > 0);
    }

    [Fact]
    public async Task WriteAsync_DuplicateNameAllocatesNewFileAndKeepsBothContents()
    {
        using var project = new TemporaryProject();
        var store = new FileArtifactStore(project.ProjectPath);

        var first = await store.WriteAsync(
            new ArtifactWriteRequest { FileName = "log.txt", Content = Encoding.UTF8.GetBytes("first") },
            CancellationToken.None);
        var second = await store.WriteAsync(
            new ArtifactWriteRequest { FileName = "log.txt", Content = Encoding.UTF8.GetBytes("second") },
            CancellationToken.None);

        Assert.Equal("log.txt", first.FileName);
        Assert.Equal("log-2.txt", second.FileName);
        Assert.NotEqual(first.RelativePath, second.RelativePath);

        var firstContent = await store.ReadAsync(first, CancellationToken.None);
        var secondContent = await store.ReadAsync(second, CancellationToken.None);
        Assert.Equal("first", Encoding.UTF8.GetString(firstContent.Bytes));
        Assert.Equal("second", Encoding.UTF8.GetString(secondContent.Bytes));
    }

    [Fact]
    public async Task WriteAsync_ConcurrentSameNameWritesAllSurvive()
    {
        using var project = new TemporaryProject();
        var store = new FileArtifactStore(project.ProjectPath);

        var references = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            store.WriteAsync(
                new ArtifactWriteRequest
                {
                    FileName = "shared.json",
                    Content = Encoding.UTF8.GetBytes("{\"index\":" + index + "}"),
                    ContentType = "application/json"
                },
                CancellationToken.None)));

        Assert.Equal(8, references.Select(item => item.RelativePath).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(8, Directory.GetFiles(project.ArtifactsRoot, "*.json").Length);

        foreach (var reference in references)
        {
            var content = await store.ReadAsync(reference, CancellationToken.None);
            Assert.Equal(reference.Sha256, content.Reference.Sha256);
            Assert.Equal(reference.SizeBytes, content.Reference.SizeBytes);
        }
    }

    [Fact]
    public async Task WriteAsync_LeavesNoTemporaryFiles()
    {
        using var project = new TemporaryProject();
        var store = new FileArtifactStore(project.ProjectPath);
        var big = new byte[128 * 1024];
        new Random(42).NextBytes(big);

        await store.WriteAsync(
            new ArtifactWriteRequest { FileName = "big.bin", Content = big },
            CancellationToken.None);

        var leftovers = Directory.GetFiles(project.ArtifactsRoot, "*.tmp", SearchOption.AllDirectories);
        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task ReadAsync_RecomputesHashFromDisk()
    {
        using var project = new TemporaryProject();
        var store = new FileArtifactStore(project.ProjectPath);
        var reference = await store.WriteAsync(
            new ArtifactWriteRequest { FileName = "value.txt", Content = Encoding.UTF8.GetBytes("before") },
            CancellationToken.None);

        var path = project.Resolve(reference.RelativePath);
        // Encoding.UTF8 供 File.WriteAllText 使用时会写入 BOM，这里验证的是磁盘内容重算，
        // 因此显式使用不带 BOM 的 UTF-8，保证断言与哈希都以纯文本为准。
        await File.WriteAllTextAsync(path, "after", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var content = await store.ReadAsync(reference, CancellationToken.None);

        Assert.Equal("after", Encoding.UTF8.GetString(content.Bytes));
        Assert.NotEqual(reference.Sha256, content.Reference.Sha256);
        Assert.Equal(ExpectedSha256(Encoding.UTF8.GetBytes("after")), content.Reference.Sha256);
    }

    [Fact]
    public async Task ReadAsync_ResolvesReferenceWithOnlyRelativePath()
    {
        using var project = new TemporaryProject();
        var store = new FileArtifactStore(project.ProjectPath);
        var written = await store.WriteAsync(
            new ArtifactWriteRequest { FileName = "only-path.bin", Content = new byte[] { 7, 8, 9 } },
            CancellationToken.None);

        var content = await store.ReadAsync(
            new ArtifactReference { RelativePath = written.RelativePath },
            CancellationToken.None);

        Assert.Equal(new byte[] { 7, 8, 9 }, content.Bytes);
        Assert.Equal("only-path.bin", content.Reference.FileName);
    }

    [Fact]
    public async Task ReadAsync_RejectsReferencesOutsideArtifactsRoot()
    {
        using var project = new TemporaryProject();
        var store = new FileArtifactStore(project.ProjectPath);

        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(
            new ArtifactReference { RelativePath = "../../outside.txt" },
            CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(
            new ArtifactReference { RelativePath = @"C:\Windows\win.ini" },
            CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() => store.ExistsAsync(
            new ArtifactReference { RelativePath = "../../../escape.json" },
            CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_MissingArtifactThrowsFileNotFound()
    {
        using var project = new TemporaryProject();
        var store = new FileArtifactStore(project.ProjectPath);
        var reference = new ArtifactReference { RelativePath = ".bdtool/artifacts/missing.txt" };

        Assert.False(await store.ExistsAsync(reference, CancellationToken.None));
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => store.ReadAsync(reference, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_EmptyReferenceThrowsArgument()
    {
        using var project = new TemporaryProject();
        var store = new FileArtifactStore(project.ProjectPath);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.ReadAsync(new ArtifactReference(), CancellationToken.None));
    }

    private static string ExpectedSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
