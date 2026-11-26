using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts.Errors;
using BDTool.Core.Workspace;

namespace BDTool.Tests.Workspace;

/// <summary>批量指纹必须逐项隔离缺失，同时对目录和越界保持整体失败。</summary>
public sealed class LocalWorkspaceFingerprintTests : IDisposable
{
    private readonly WorkspaceTestProject _project = new();

    [Fact]
    public async Task FingerprintAsync_ReportsExistingAndMissingEntriesInRequestOrder()
    {
        _project.WriteText("Assets/Existing.cs", "hello");

        var result = await _project.CreateKernel().FingerprintAsync(
            _project.FingerprintRequest("Assets/Existing.cs", "Assets/Missing.cs"),
            CancellationToken.None);

        Assert.Equal(2, result.Files.Count);
        Assert.Equal("Assets/Existing.cs", result.Files[0].RelativePath);
        Assert.True(result.Files[0].Exists);
        Assert.Equal(5, result.Files[0].Length);
        Assert.Equal(WorkspaceTestProject.Sha256("hello"), result.Files[0].Sha256);

        Assert.Equal("Assets/Missing.cs", result.Files[1].RelativePath);
        Assert.False(result.Files[1].Exists);
        Assert.Equal(0, result.Files[1].Length);
        Assert.Equal(string.Empty, result.Files[1].Sha256);
    }

    [Fact]
    public async Task FingerprintAsync_ChangesWhenContentChanges()
    {
        _project.WriteText("file.txt", "aaa");
        var kernel = _project.CreateKernel();

        var before = await kernel.FingerprintAsync(
            _project.FingerprintRequest("file.txt"),
            CancellationToken.None);

        _project.WriteText("file.txt", "bbb");

        var after = await kernel.FingerprintAsync(
            _project.FingerprintRequest("file.txt"),
            CancellationToken.None);

        Assert.Equal(3, before.Files[0].Length);
        Assert.Equal(3, after.Files[0].Length);
        Assert.NotEqual(before.Files[0].Sha256, after.Files[0].Sha256);
        Assert.Equal(WorkspaceTestProject.Sha256("bbb"), after.Files[0].Sha256);
    }

    [Fact]
    public async Task FingerprintAsync_UsesRawBytesIncludingBom()
    {
        _project.WriteText("bom.txt", "hello", withBom: true);

        var result = await _project.CreateKernel().FingerprintAsync(
            _project.FingerprintRequest("bom.txt"),
            CancellationToken.None);

        var raw = _project.ReadBytes("bom.txt");
        Assert.Equal(raw.Length, result.Files[0].Length);
        Assert.Equal(WorkspaceTestProject.Sha256(raw), result.Files[0].Sha256);
    }

    [Fact]
    public async Task FingerprintAsync_RejectsDirectoryTarget()
    {
        Directory.CreateDirectory(_project.Resolve("Assets"));

        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().FingerprintAsync(
                _project.FingerprintRequest("Assets"),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("Assets", exception.Details["relativePath"]);
    }

    [Fact]
    public async Task FingerprintAsync_RejectsPathOutsideProject()
    {
        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().FingerprintAsync(
                _project.FingerprintRequest("../outside.txt"),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
    }

    [Fact]
    public async Task FingerprintAsync_RejectsEmptyRelativePath()
    {
        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().FingerprintAsync(
                _project.FingerprintRequest(string.Empty),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
    }

    [Fact]
    public async Task FingerprintAsync_HonorsCancellation()
    {
        _project.WriteText("file.txt", "content");
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _project.CreateKernel().FingerprintAsync(
                _project.FingerprintRequest("file.txt"),
                source.Token));
    }

    public void Dispose()
    {
        _project.Dispose();
    }
}
