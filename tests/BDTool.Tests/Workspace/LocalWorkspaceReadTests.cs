using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Workspace;
using BDTool.Core.Workspace;
using BDTool.Provider.Local;

namespace BDTool.Tests.Workspace;

/// <summary>workspace.read 的 UTF-8、BOM、换行、上限与错误分类边界。</summary>
public sealed class LocalWorkspaceReadTests : IDisposable
{
    private readonly WorkspaceTestProject _project = new();

    [Fact]
    public async Task ReadAsync_StripsBomButFingerprintsOriginalBytes()
    {
        var body = Encoding.UTF8.GetBytes("hello");
        var raw = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray();
        _project.WriteBytes("Assets/Bom.txt", raw);

        var result = await _project.CreateKernel().ReadAsync(
            _project.ReadRequest("Assets\\Bom.txt"),
            CancellationToken.None);

        Assert.Equal("Assets/Bom.txt", result.RelativePath);
        Assert.Equal("hello", result.Content);
        Assert.Equal(body.Length, result.Length);
        Assert.Equal(WorkspaceTestProject.Sha256(raw), result.Sha256);
        Assert.NotEqual(WorkspaceTestProject.Sha256(body), result.Sha256);
    }

    [Fact]
    public async Task ReadAsync_AcceptsBomOnlyFileAsEmptyText()
    {
        _project.WriteBytes("empty.txt", new byte[] { 0xEF, 0xBB, 0xBF });

        var result = await _project.CreateKernel().ReadAsync(
            _project.ReadRequest("empty.txt"),
            CancellationToken.None);

        Assert.Equal(string.Empty, result.Content);
        Assert.Equal(0, result.Length);
        Assert.Equal(WorkspaceTestProject.Sha256(new byte[] { 0xEF, 0xBB, 0xBF }), result.Sha256);
    }

    [Fact]
    public async Task ReadAsync_PreservesCrlfAndLfExactly()
    {
        const string text = "first\r\nsecond\nthird";
        _project.WriteText("mixed.txt", text);

        var result = await _project.CreateKernel().ReadAsync(
            _project.ReadRequest("mixed.txt"),
            CancellationToken.None);

        Assert.Equal(text, result.Content);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), result.Length);
    }

    [Fact]
    public async Task ReadAsync_ReportsUtf8ByteLengthRatherThanCharacterCount()
    {
        const string text = "中文🙂";
        _project.WriteText("unicode.txt", text);

        var result = await _project.CreateKernel().ReadAsync(
            _project.ReadRequest("unicode.txt"),
            CancellationToken.None);

        Assert.Equal(text, result.Content);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), result.Length);
        Assert.True(result.Length > text.Length);
    }

    [Fact]
    public async Task ReadAsync_RejectsFileAboveLimitWithoutTruncating()
    {
        _project.WriteText("large.txt", "12345");
        var kernel = _project.CreateKernel(options => options.MaxReadBytes = 4);

        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            kernel.ReadAsync(_project.ReadRequest("large.txt"), CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("4", exception.Details["maxReadBytes"]);
        Assert.Equal("large.txt", exception.Details["relativePath"]);
    }

    [Fact]
    public async Task ReadAsync_RejectsInvalidUtf8()
    {
        _project.WriteBytes("invalid.txt", new byte[] { 0x41, 0xC3, 0x28 });

        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ReadAsync(
                _project.ReadRequest("invalid.txt"),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("invalid-utf8", exception.Details["reason"]);
    }

    [Fact]
    public async Task ReadAsync_RejectsNulCharacter()
    {
        _project.WriteBytes("nul-char.txt", Encoding.UTF8.GetBytes("a\0b"));

        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ReadAsync(
                _project.ReadRequest("nul-char.txt"),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("nul-character", exception.Details["reason"]);
    }

    [Fact]
    public async Task ReadAsync_RejectsDirectoryTarget()
    {
        Directory.CreateDirectory(_project.Resolve("Assets"));

        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ReadAsync(
                _project.ReadRequest("Assets"),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("Assets", exception.Details["relativePath"]);
    }

    [Fact]
    public async Task ReadAsync_ReturnsNotFoundForMissingFile()
    {
        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ReadAsync(
                _project.ReadRequest("Assets/Missing.cs"),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.NotFound, exception.Code);
        Assert.Equal("Assets/Missing.cs", exception.Details["relativePath"]);
    }

    [ReparsePointFact]
    public async Task ReadAsync_RejectsReparsePointInTargetPath()
    {
        var targetFile = Path.Combine(Path.GetTempPath(), "bdtool-read-target-" + Guid.NewGuid().ToString("n") + ".txt");
        await File.WriteAllTextAsync(targetFile, "secret");
        var fileLink = _project.Resolve("link.txt");
        if (WorkspaceReparsePoints.TryCreateFileLink(fileLink, targetFile))
        {
            try
            {
                await AssertReparseRejectedAsync("link.txt");
            }
            finally
            {
                File.Delete(fileLink);
                File.Delete(targetFile);
            }

            return;
        }

        // Windows 普通用户通常无法创建文件符号链接；退回 junction 验证祖先段 reparse
        // 同样被拒绝，从而实际覆盖 Read 的 reparse 分支。两者都不可用时显式跳过。
        File.Delete(targetFile);
        var targetDirectory = Path.Combine(Path.GetTempPath(), "bdtool-read-dir-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(targetDirectory);
        await File.WriteAllTextAsync(Path.Combine(targetDirectory, "secret.txt"), "secret");
        var directoryLink = _project.Resolve("linked-directory");
        // 发现阶段的 ReparsePointFact 已确认目录链接可用；运行阶段失败说明环境异常。
        if (!WorkspaceReparsePoints.TryCreateDirectoryLink(directoryLink, targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
            Assert.Fail("发现阶段探测显示目录链接可用，但运行阶段创建失败。");
        }

        try
        {
            await AssertReparseRejectedAsync("linked-directory/secret.txt");
        }
        finally
        {
            Directory.Delete(directoryLink);
            Directory.Delete(targetDirectory, recursive: true);
        }
    }

    private async Task AssertReparseRejectedAsync(string relativePath)
    {
        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ReadAsync(
                _project.ReadRequest(relativePath),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("ReparsePoint", exception.Details["violation"]);
    }

    [Fact]
    public async Task ReadAsync_HonorsCancellationBeforeOpeningFile()
    {
        _project.WriteText("cancelled.txt", "content");
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _project.CreateKernel().ReadAsync(_project.ReadRequest("cancelled.txt"), source.Token));
    }

    [Fact]
    public async Task ReadAsync_AcceptsFileExactlyAtDefaultLimit()
    {
        var content = new string('a', (int)LocalWorkspaceOptions.DefaultMaxBytes);
        _project.WriteText("boundary.txt", content);

        var result = await _project.CreateKernel().ReadAsync(
            _project.ReadRequest("boundary.txt"),
            CancellationToken.None);

        // 上限是“允许读取的最大字节数”，等于上限时必须成功且内容完整。
        Assert.Equal(LocalWorkspaceOptions.DefaultMaxBytes, result.Length);
        Assert.Equal(content, result.Content);
    }

    [Fact]
    public async Task ReadAsync_RejectsFileOneByteAboveDefaultLimit()
    {
        _project.WriteText("over.txt", new string('a', (int)LocalWorkspaceOptions.DefaultMaxBytes + 1));

        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ReadAsync(
                _project.ReadRequest("over.txt"),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("1048576", exception.Details["maxReadBytes"]);
    }

    [Fact]
    public async Task ReadAsync_SupportsMaxReadBytesAboveInt32MaxValue()
    {
        _project.WriteText("small.txt", "small");

        // 容量参数是 int；上限远大于 int.MaxValue 时读取仍必须正常，而不是溢出成负容量。
        var kernel = _project.CreateKernel(options => options.MaxReadBytes = long.MaxValue);
        var result = await kernel.ReadAsync(
            _project.ReadRequest("small.txt"),
            CancellationToken.None);

        Assert.Equal("small", result.Content);
        Assert.Equal(5, result.Length);
    }

    public void Dispose()
    {
        _project.Dispose();
    }
}
