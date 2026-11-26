using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts.Errors;
using BDTool.Core.Workspace;
using BDTool.Provider.Local;

namespace BDTool.Tests.Workspace;

/// <summary>workspace.apply 的创建、CAS、dry-run、原子替换与失败清理语义。</summary>
public sealed class LocalWorkspaceApplyTests : IDisposable
{
    private readonly WorkspaceTestProject _project = new();

    [Fact]
    public async Task ApplyAsync_CreatesMissingFileAsUtf8WithoutBom()
    {
        const string content = "first\r\n第二行\n";

        var result = await _project.CreateKernel().ApplyAsync(
            _project.ChangeRequest("Assets/New.txt", content, createIfMissing: true),
            CancellationToken.None);

        Assert.True(result.Changed);
        Assert.True(result.Created);
        Assert.False(result.DryRun);
        Assert.Null(result.PreviousSha256);
        Assert.Equal(WorkspaceTestProject.Sha256(content), result.NewSha256);
        Assert.Equal(Encoding.UTF8.GetBytes(content), _project.ReadBytes("Assets/New.txt"));
        Assert.Empty(_project.TemporaryFiles());
    }

    [Fact]
    public async Task ApplyAsync_CreatesMissingParentDirectoriesWhenAllowed()
    {
        var result = await _project.CreateKernel().ApplyAsync(
            _project.ChangeRequest("Assets/Generated/Deep/New.cs", "content", createIfMissing: true),
            CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal("content", _project.ReadText("Assets/Generated/Deep/New.cs"));
    }

    [Fact]
    public async Task ApplyAsync_DryRunDoesNotTouchDisk()
    {
        var result = await _project.CreateKernel().ApplyAsync(
            _project.ChangeRequest(
                "Assets/Generated/New.cs",
                "planned",
                createIfMissing: true,
                dryRun: true),
            CancellationToken.None);

        Assert.True(result.Changed);
        Assert.True(result.Created);
        Assert.True(result.DryRun);
        Assert.Equal(WorkspaceTestProject.Sha256("planned"), result.NewSha256);
        Assert.False(File.Exists(_project.Resolve("Assets/Generated/New.cs")));
        Assert.False(Directory.Exists(_project.Resolve("Assets")));
        Assert.Empty(_project.TemporaryFiles());
    }

    [Fact]
    public async Task ApplyAsync_ReturnsNotFoundWhenMissingAndCreationIsDisabled()
    {
        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ApplyAsync(
                _project.ChangeRequest("Missing.cs", "content", createIfMissing: false),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.NotFound, exception.Code);
        Assert.False(File.Exists(_project.Resolve("Missing.cs")));
    }

    [Fact]
    public async Task ApplyAsync_RequiresExpectedHashForExistingFile()
    {
        _project.WriteText("existing.txt", "old");

        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ApplyAsync(
                _project.ChangeRequest("existing.txt", "new"),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("old", _project.ReadText("existing.txt"));
    }

    [Fact]
    public async Task ApplyAsync_SameContentDoesNotRewriteOrChangeTimestamp()
    {
        _project.WriteText("same.txt", "unchanged");
        var expected = _project.Hash("same.txt");
        var timestamp = File.GetLastWriteTimeUtc(_project.Resolve("same.txt"));

        var result = await _project.CreateKernel().ApplyAsync(
            _project.ChangeRequest("same.txt", "unchanged", expected),
            CancellationToken.None);

        Assert.False(result.Changed);
        Assert.False(result.Created);
        Assert.False(result.DryRun);
        Assert.Equal(expected, result.PreviousSha256);
        Assert.Equal(expected, result.NewSha256);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(_project.Resolve("same.txt")));
        Assert.Empty(_project.TemporaryFiles());
    }

    [Fact]
    public async Task ApplyAsync_RejectsStaleExpectedHashWithoutSideEffects()
    {
        _project.WriteText("cas.txt", "current");
        var actual = _project.Hash("cas.txt");

        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ApplyAsync(
                _project.ChangeRequest("cas.txt", "new", new string('0', 64)),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.PreconditionFailed, exception.Code);
        Assert.Equal(new string('0', 64), exception.Details["expectedSha256"]);
        Assert.Equal(actual, exception.Details["actualSha256"]);
        Assert.Equal("current", _project.ReadText("cas.txt"));
        Assert.Empty(_project.TemporaryFiles());
    }

    [Fact]
    public async Task ApplyAsync_OverwritesWhenExpectedHashMatches()
    {
        _project.WriteText("overwrite.txt", "old\r\n");
        var previous = _project.Hash("overwrite.txt");

        var result = await _project.CreateKernel().ApplyAsync(
            _project.ChangeRequest("overwrite.txt", "new\n", previous),
            CancellationToken.None);

        Assert.True(result.Changed);
        Assert.False(result.Created);
        Assert.Equal(previous, result.PreviousSha256);
        Assert.Equal(WorkspaceTestProject.Sha256("new\n"), result.NewSha256);
        Assert.Equal("new\n", _project.ReadText("overwrite.txt"));
        Assert.Equal(Encoding.UTF8.GetBytes("new\n"), _project.ReadBytes("overwrite.txt"));
        Assert.Empty(_project.TemporaryFiles());
    }

    [Fact]
    public async Task ApplyAsync_RemovesBomWhenOverwritingBomFile()
    {
        _project.WriteText("bom.txt", "old", withBom: true);
        var previous = _project.Hash("bom.txt");
        Assert.Equal(3 + 3, _project.ReadBytes("bom.txt").Length);

        var result = await _project.CreateKernel().ApplyAsync(
            _project.ChangeRequest("bom.txt", "new", previous),
            CancellationToken.None);

        Assert.Equal(previous, result.PreviousSha256);
        Assert.Equal(Encoding.UTF8.GetBytes("new"), _project.ReadBytes("bom.txt"));
    }

    [Fact]
    public async Task ApplyAsync_DryRunWithMatchingHashReportsPlanWithoutWriting()
    {
        _project.WriteText("plan.txt", "old");
        var previous = _project.Hash("plan.txt");

        var result = await _project.CreateKernel().ApplyAsync(
            _project.ChangeRequest("plan.txt", "planned", previous, dryRun: true),
            CancellationToken.None);

        Assert.True(result.Changed);
        Assert.False(result.Created);
        Assert.True(result.DryRun);
        Assert.Equal(previous, result.PreviousSha256);
        Assert.Equal(WorkspaceTestProject.Sha256("planned"), result.NewSha256);
        Assert.Equal("old", _project.ReadText("plan.txt"));
        Assert.Empty(_project.TemporaryFiles());
    }

    [Fact]
    public async Task ApplyAsync_DryRunStillReportsStaleHash()
    {
        _project.WriteText("plan.txt", "old");

        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ApplyAsync(
                _project.ChangeRequest(
                    "plan.txt",
                    "planned",
                    new string('a', 64),
                    dryRun: true),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.PreconditionFailed, exception.Code);
        Assert.Equal("old", _project.ReadText("plan.txt"));
    }

    [Fact]
    public async Task ApplyAsync_RejectsNulContent()
    {
        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ApplyAsync(
                _project.ChangeRequest("nul.txt", "a\0b", createIfMissing: true),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("nul-character", exception.Details["reason"]);
        Assert.False(File.Exists(_project.Resolve("nul.txt")));
        Assert.Empty(_project.TemporaryFiles());
    }

    [Fact]
    public async Task ApplyAsync_RejectsLoneSurrogate()
    {
        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ApplyAsync(
                _project.ChangeRequest("surrogate.txt", "\uD800", createIfMissing: true),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("invalid-utf16", exception.Details["reason"]);
        Assert.False(File.Exists(_project.Resolve("surrogate.txt")));
    }

    [Fact]
    public async Task ApplyAsync_CancellationBeforeCommitLeavesNoSideEffects()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _project.CreateKernel().ApplyAsync(
                _project.ChangeRequest(
                    "Assets/Generated/New.cs",
                    "content",
                    createIfMissing: true),
                source.Token));

        Assert.False(File.Exists(_project.Resolve("Assets/Generated/New.cs")));
        Assert.False(Directory.Exists(_project.Resolve("Assets")));
        Assert.Empty(_project.TemporaryFiles());
    }

    [Fact]
    public async Task ApplyAsync_RejectsDirectoryTarget()
    {
        Directory.CreateDirectory(_project.Resolve("Assets"));

        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ApplyAsync(
                _project.ChangeRequest("Assets", "content", createIfMissing: true),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
    }

    [Fact]
    public async Task ApplyAsync_RejectsPathOutsideProjectBeforeCreatingAnything()
    {
        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ApplyAsync(
                _project.ChangeRequest("../escape.txt", "content", createIfMissing: true),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_project.Root)!, "escape.txt")));
        Assert.Empty(_project.TemporaryFiles());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("Assets/../file.txt")]
    [InlineData("Assets/./file.txt")]
    [InlineData("Assets//file.txt")]
    [InlineData("Assets/file.txt:stream")]
    [InlineData("CON")]
    [InlineData("Assets/con.txt")]
    [InlineData("Assets/trailing.")]
    [InlineData("Assets/trailing ")]
    [InlineData("Assets/bad*name.txt")]
    public async Task ApplyAsync_RejectsInvalidRelativePathsBeforeWriting(string relativePath)
    {
        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ApplyAsync(
                _project.ChangeRequest(relativePath, "content", createIfMissing: true),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Empty(_project.TemporaryFiles());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_project.Root));
    }

    [Fact]
    public async Task ApplyAsync_AcceptsContentExactlyAtApplyLimit()
    {
        // 上限按编码后字节数计算；1 MiB ASCII 恰好等于默认 MaxApplyBytes，必须允许写入。
        var content = new string('a', (int)LocalWorkspaceOptions.DefaultMaxApplyBytes);

        var result = await _project.CreateKernel().ApplyAsync(
            _project.ChangeRequest("limit/boundary.txt", content, createIfMissing: true),
            CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal(
            LocalWorkspaceOptions.DefaultMaxApplyBytes,
            _project.ReadBytes("limit/boundary.txt").LongLength);
    }

    [Fact]
    public async Task ApplyAsync_RejectsContentOneByteAboveApplyLimitWithoutSideEffects()
    {
        var content = new string('a', (int)LocalWorkspaceOptions.DefaultMaxApplyBytes + 1);

        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ApplyAsync(
                _project.ChangeRequest("limit/too-large.txt", content, createIfMissing: true),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("content-too-large", exception.Details["reason"]);
        Assert.Equal("1048577", exception.Details["contentBytes"]);
        Assert.Equal("1048576", exception.Details["maxApplyBytes"]);
        // 超限必须在建目录之前拒绝，否则会留下一次失败写入的空目录。
        Assert.False(Directory.Exists(_project.Resolve("limit")));
        Assert.Empty(_project.TemporaryFiles());
    }

    [Fact]
    public async Task ApplyAsync_RejectsOverLimitDryRunWithoutSideEffects()
    {
        var content = new string('a', (int)LocalWorkspaceOptions.DefaultMaxApplyBytes + 1);

        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ApplyAsync(
                _project.ChangeRequest(
                    "dry/too-large.txt",
                    content,
                    createIfMissing: true,
                    dryRun: true),
                CancellationToken.None));

        // dry-run 与实写使用同一内容上限，避免“计划成功、实写失败”的行为分叉。
        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("content-too-large", exception.Details["reason"]);
        Assert.False(Directory.Exists(_project.Resolve("dry")));
        Assert.Empty(_project.TemporaryFiles());
    }

    [Fact]
    public async Task ApplyAsync_HonorsCustomSmallApplyLimit()
    {
        var kernel = _project.CreateKernel(options => options.MaxApplyBytes = 4);

        var accepted = await kernel.ApplyAsync(
            _project.ChangeRequest("custom/ok.txt", "abcd", createIfMissing: true),
            CancellationToken.None);
        Assert.True(accepted.Created);
        Assert.Equal("abcd", _project.ReadText("custom/ok.txt"));

        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            kernel.ApplyAsync(
                _project.ChangeRequest("custom/too-large.txt", "abcde", createIfMissing: true),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("5", exception.Details["contentBytes"]);
        Assert.Equal("4", exception.Details["maxApplyBytes"]);
        Assert.False(File.Exists(_project.Resolve("custom/too-large.txt")));
    }

    [Fact]
    public async Task ApplyAsync_RejectsParentSegmentThatIsAFileForDryRunAndWrite()
    {
        _project.WriteText("blocked", "not a directory");

        var dryRun = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ApplyAsync(
                _project.ChangeRequest(
                    "blocked/child.txt",
                    "content",
                    createIfMissing: true,
                    dryRun: true),
                CancellationToken.None));

        // dry-run 必须与实写给出同样的结构错误，不能先报告可写再在提交阶段失败。
        Assert.Equal(ErrorCodes.InvalidArgument, dryRun.Code);
        Assert.Equal("parent-not-directory", dryRun.Details["reason"]);
        Assert.Equal("blocked", dryRun.Details["conflictPath"]);

        var write = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ApplyAsync(
                _project.ChangeRequest("blocked/child.txt", "content", createIfMissing: true),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, write.Code);
        Assert.Equal("parent-not-directory", write.Details["reason"]);
        Assert.Equal("not a directory", _project.ReadText("blocked"));
        Assert.False(File.Exists(_project.Resolve("blocked/child.txt")));
        Assert.Empty(_project.TemporaryFiles());
    }

    public void Dispose()
    {
        _project.Dispose();
    }
}
