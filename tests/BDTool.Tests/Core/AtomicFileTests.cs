using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Core.IO;

namespace BDTool.Tests.Core;

/// <summary>共享原子写的持久化与失败清理边界。</summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "bdtool-atomic-tests",
        Guid.NewGuid().ToString("n"));

    public AtomicFileTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task WriteUtf8Async_WritesWithoutBomAndReplacesExistingContent()
    {
        var path = Path.Combine(_directory, "record.json");
        await File.WriteAllTextAsync(path, "old", Encoding.UTF8);

        var written = await AtomicFile.WriteUtf8Async(
            path,
            "新内容",
            overwrite: true,
            CancellationToken.None);

        Assert.True(written);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("新内容", Encoding.UTF8.GetString(bytes));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_DoesNotOverwriteWhenOverwriteIsFalse()
    {
        var path = Path.Combine(_directory, "artifact.txt");
        await File.WriteAllTextAsync(path, "existing");

        var written = await AtomicFile.WriteAsync(
            path,
            Encoding.UTF8.GetBytes("candidate"),
            overwrite: false,
            CancellationToken.None);

        Assert.False(written);
        Assert.Equal("existing", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_CancellationBeforeCommitLeavesNoPartialOrTemporaryFile()
    {
        var path = Path.Combine(_directory, "cancelled.txt");
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AtomicFile.WriteAsync(path, Encoding.UTF8.GetBytes("partial"), source.Token));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public async Task WriteUtf8Async_RejectsInvalidUtf16InsteadOfWritingReplacementCharacters()
    {
        var path = Path.Combine(_directory, "invalid.txt");

        await Assert.ThrowsAsync<EncoderFallbackException>(() =>
            AtomicFile.WriteUtf8Async(path, "\uD800", overwrite: true, CancellationToken.None));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
