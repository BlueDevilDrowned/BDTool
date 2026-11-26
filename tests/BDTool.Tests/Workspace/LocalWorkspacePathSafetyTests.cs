using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts.Errors;
using BDTool.Core.Workspace;
using BDTool.Provider.Local;

namespace BDTool.Tests.Workspace;

/// <summary>项目根与相对路径的稳定失败语义，确保所有入口都经过 PathPolicy。</summary>
public sealed class LocalWorkspacePathSafetyTests : IDisposable
{
    private readonly WorkspaceTestProject _project = new();

    [Fact]
    public async Task AllOperations_ReportProjectNotFoundForMissingDirectory()
    {
        var missing = Path.Combine(Path.GetTempPath(), "bdtool-missing-" + Guid.NewGuid().ToString("n"));
        var kernel = new LocalWorkspaceKernel(missing);

        var read = await Assert.ThrowsAsync<WorkspaceException>(() =>
            kernel.ReadAsync(
                new BDTool.Contracts.Workspace.WorkspaceReadRequest { RelativePath = "file.txt" },
                CancellationToken.None));
        var search = await Assert.ThrowsAsync<WorkspaceException>(() =>
            kernel.SearchAsync(
                new BDTool.Contracts.Workspace.WorkspaceSearchRequest { Query = "x" },
                CancellationToken.None));
        var fingerprint = await Assert.ThrowsAsync<WorkspaceException>(() =>
            kernel.FingerprintAsync(
                new BDTool.Contracts.Workspace.WorkspaceFingerprintRequest
                {
                    RelativePaths = { "file.txt" }
                },
                CancellationToken.None));
        var apply = await Assert.ThrowsAsync<WorkspaceException>(() =>
            kernel.ApplyAsync(
                new BDTool.Contracts.Workspace.WorkspaceChangeRequest
                {
                    RelativePath = "file.txt",
                    Content = "x",
                    CreateIfMissing = true
                },
                CancellationToken.None));

        Assert.Equal(ErrorCodes.ProjectNotFound, read.Code);
        Assert.Equal(ErrorCodes.ProjectNotFound, search.Code);
        Assert.Equal(ErrorCodes.ProjectNotFound, fingerprint.Code);
        Assert.Equal(ErrorCodes.ProjectNotFound, apply.Code);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task ReadAsync_RejectsProjectPathThatIsAFile()
    {
        var filePath = Path.Combine(Path.GetTempPath(), "bdtool-project-file-" + Guid.NewGuid().ToString("n") + ".txt");
        await File.WriteAllTextAsync(filePath, "not a directory");
        try
        {
            var kernel = new LocalWorkspaceKernel(filePath);

            var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
                kernel.ReadAsync(
                    new BDTool.Contracts.Workspace.WorkspaceReadRequest { RelativePath = "file.txt" },
                    CancellationToken.None));

            Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
            Assert.Equal("not-a-directory", exception.Details["reason"]);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [ReparsePointFact]
    public async Task ReadAsync_RejectsProjectRootThatIsAReparsePoint()
    {
        var target = Path.Combine(Path.GetTempPath(), "bdtool-project-target-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(target);
        var link = Path.Combine(Path.GetTempPath(), "bdtool-project-link-" + Guid.NewGuid().ToString("n"));

        // 发现阶段的 ReparsePointFact 已确认目录链接可用；运行阶段失败说明环境异常。
        if (!WorkspaceReparsePoints.TryCreateDirectoryLink(link, target))
        {
            Directory.Delete(target, recursive: true);
            Assert.Fail("发现阶段探测显示目录链接可用，但运行阶段创建失败。");
        }

        try
        {
            var kernel = new LocalWorkspaceKernel(link);

            var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
                kernel.ReadAsync(
                    new BDTool.Contracts.Workspace.WorkspaceReadRequest { RelativePath = "file.txt" },
                    CancellationToken.None));

            Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
            Assert.Equal("reparse-point", exception.Details["reason"]);
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_RejectsProjectPathThatDoesNotMatchKernelBinding()
    {
        using var other = new WorkspaceTestProject();
        var request = _project.ReadRequest("file.txt");
        request.ProjectPath = other.Root;

        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ReadAsync(request, CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal(other.Root, exception.Details["requestedProjectPath"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("/absolute.txt")]
    [InlineData("\\server\\share\\file.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:relative.txt")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape.txt")]
    [InlineData("Assets/../escape.txt")]
    [InlineData("Assets/./file.txt")]
    [InlineData("Assets//file.txt")]
    [InlineData("Assets/file.txt:stream")]
    [InlineData("CON")]
    [InlineData("Assets/con.txt")]
    [InlineData("Assets/COM1.log")]
    [InlineData("Assets/trailing.")]
    [InlineData("Assets/trailing ")]
    [InlineData("Assets/bad*name.txt")]
    [InlineData("Assets/bad?name.txt")]
    [InlineData("Assets/\u0001.txt")]
    public async Task ReadAsync_RejectsUnsafeRelativePaths(string relativePath)
    {
        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().ReadAsync(
                _project.ReadRequest(relativePath),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("InvalidPath", exception.Details["violation"]);
    }

    [Fact]
    public void Constructor_RejectsEmptyProjectPath()
    {
        var exception = Assert.Throws<WorkspaceException>(() => new LocalWorkspaceKernel(string.Empty));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
    }

    [Fact]
    public async Task Kernel_BindsToConstructorProjectWhenOptionsPathIsEmpty()
    {
        var kernel = new LocalWorkspaceKernel(_project.Root, new LocalWorkspaceOptions());
        _project.WriteText("file.txt", "content");

        var result = await kernel.ReadAsync(
            _project.ReadRequest("file.txt"),
            CancellationToken.None);

        Assert.Equal("content", result.Content);
    }

    public void Dispose()
    {
        _project.Dispose();
    }
}
