using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Application;
using BDTool.Contracts;
using BDTool.Contracts.Artifacts;
using BDTool.Contracts.Capabilities;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Projects;
using BDTool.Contracts.Providers;
using BDTool.Contracts.Workspace;
using BDTool.Core.Workspace;
using BDTool.Operations.Catalog;
using BDTool.Operations.Diagnostics;
using BDTool.Operations.Operations;
using BDTool.Operations.Projects;
using BDTool.Operations.Workspace;
using BDTool.Provider.Local;
using BDTool.Tests.Support;

namespace BDTool.Tests.Catalog;

/// <summary>
/// M1 Handler 的真实输入解析、typed 结果和 Provider 错误映射。
/// 这些测试覆盖 Operations 层本身，不依赖 CLI 文本输出。
/// </summary>
public sealed class M1OperationHandlerTests
{
    private static IOperationCatalog Catalog() => OperationCatalogLoader.Load(RepoPaths.CatalogFile);

    [Fact]
    public async Task OperationsList_ReturnsTypedTwelveOperationSummaries()
    {
        var handler = new OperationsListHandler(Catalog());

        var result = await handler.ExecuteAsync(
            Request("operations.list", "{}"),
            new TestContext(new ThrowingWorkspaceKernel(new NotSupportedException()), new EmptyCapabilities()),
            CancellationToken.None);

        var typed = Assert.IsType<OperationResult<OperationsListData>>(result);
        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.Equal(12, typed.Data.Operations.Count);
        Assert.Contains(typed.Data.Operations, item => item.Id == "workspace.apply" && item.Implemented);
        Assert.Contains(
            typed.Data.Operations,
            item => item.Id == "project.inspect" && item.Milestone == "M2");
        Assert.Contains(
            typed.Data.Operations,
            item => item.Id == "project.probe"
                    && item.Milestone == "M2"
                    && item.Implemented
                    && item.Availability == "available");
        Assert.Contains(
            typed.Data.Operations,
            item => item.Id == "project.asset-snapshot"
                    && item.Milestone == "M3"
                    && item.Implemented
                    && item.Availability == "available");
        Assert.Contains(
            typed.Data.Operations,
            item => item.Id == "project.prefab-set-active"
                    && item.Effect == "editor-write"
                    && item.Milestone == "M3"
                    && item.Implemented
                    && item.Availability == "available");
    }

    [Fact]
    public async Task OperationsDescribe_ReturnsDetailAndRejectsUnknownIdAndExtraFields()
    {
        var handler = new OperationsDescribeHandler(Catalog());

        var detailResult = await handler.ExecuteAsync(
            Request("operations.describe", "{\"operationId\":\"workspace.read\"}"),
            new TestContext(new ThrowingWorkspaceKernel(new NotSupportedException()), new EmptyCapabilities()),
            CancellationToken.None);
        var detail = Assert.IsType<OperationResult<OperationsDescribeData>>(detailResult);
        Assert.Equal(OperationStatus.Completed, detail.Status);
        Assert.Equal("workspace.read", detail.Data.Operation.Id);
        Assert.False(string.IsNullOrWhiteSpace(detail.Data.Operation.InputSchemaJson));
        Assert.False(string.IsNullOrWhiteSpace(detail.Data.Operation.ResultSchemaJson));

        var unknownResult = await handler.ExecuteAsync(
            Request("operations.describe", "{\"operationId\":\"missing.operation\"}"),
            new TestContext(new ThrowingWorkspaceKernel(new NotSupportedException()), new EmptyCapabilities()),
            CancellationToken.None);
        var unknown = Assert.IsType<OperationResult<OperationsDescribeData>>(unknownResult);
        Assert.Equal(OperationStatus.Failed, unknown.Status);
        Assert.Equal(ErrorCodes.NotFound, unknown.Errors[0].Code);
        Assert.Equal("missing.operation", unknown.Errors[0].Details["operationId"]);

        var extraResult = await handler.ExecuteAsync(
            Request("operations.describe", "{\"operationId\":\"workspace.read\",\"extra\":1}"),
            new TestContext(new ThrowingWorkspaceKernel(new NotSupportedException()), new EmptyCapabilities()),
            CancellationToken.None);
        var extra = Assert.IsType<OperationResult<OperationsDescribeData>>(extraResult);
        Assert.Equal(ErrorCodes.InvalidArgument, extra.Errors[0].Code);
    }

    [Fact]
    public async Task WorkspaceRead_StripsBomAndHashesRawDiskBytes()
    {
        using var project = new TemporaryProjectDirectory();
        var raw = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("hello\r\n"))
            .ToArray();
        Directory.CreateDirectory(Path.Combine(project.ProjectPath, "Assets"));
        await File.WriteAllBytesAsync(Path.Combine(project.ProjectPath, "Assets", "bom.txt"), raw);

        var result = await new WorkspaceReadHandler().ExecuteAsync(
            Request("workspace.read", "{\"relativePath\":\"Assets/bom.txt\"}", project.ProjectPath),
            TestContext.ForProject(project.ProjectPath),
            CancellationToken.None);

        var typed = Assert.IsType<OperationResult<WorkspaceReadResult>>(result);
        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.Equal("hello\r\n", typed.Data.Content);
        Assert.Equal(raw.Length - 3, typed.Data.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(), typed.Data.Sha256);
    }

    [Fact]
    public async Task WorkspaceSearch_ReturnsTypedMatchesExcludesGeneratedDirectoriesAndTruncates()
    {
        using var project = new TemporaryProjectDirectory();
        Directory.CreateDirectory(Path.Combine(project.ProjectPath, "Assets"));
        Directory.CreateDirectory(Path.Combine(project.ProjectPath, "bin"));
        await File.WriteAllTextAsync(Path.Combine(project.ProjectPath, "Assets", "a.txt"), "needle\nneedle\n");
        await File.WriteAllTextAsync(Path.Combine(project.ProjectPath, "bin", "skip.txt"), "needle\n");

        var result = await new WorkspaceSearchHandler().ExecuteAsync(
            Request(
                "workspace.search",
                "{\"query\":\"needle\",\"maxResults\":1}",
                project.ProjectPath),
            TestContext.ForProject(project.ProjectPath),
            CancellationToken.None);

        var typed = Assert.IsType<OperationResult<WorkspaceSearchResult>>(result);
        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.Single(typed.Data.Matches);
        Assert.True(typed.Data.Truncated);
        Assert.DoesNotContain(typed.Data.Matches, match => match.RelativePath.StartsWith("bin/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkspaceFingerprint_ReportsExistsAndMissingTypedEntries()
    {
        using var project = new TemporaryProjectDirectory();
        Directory.CreateDirectory(Path.Combine(project.ProjectPath, "Assets"));
        await File.WriteAllTextAsync(Path.Combine(project.ProjectPath, "Assets", "a.txt"), "content\n");

        var result = await new WorkspaceFingerprintHandler().ExecuteAsync(
            Request(
                "workspace.fingerprint",
                "{\"relativePaths\":[\"Assets/a.txt\",\"missing.txt\"]}",
                project.ProjectPath),
            TestContext.ForProject(project.ProjectPath),
            CancellationToken.None);

        var typed = Assert.IsType<OperationResult<WorkspaceFingerprintResult>>(result);
        Assert.Equal(2, typed.Data.Files.Count);
        Assert.True(typed.Data.Files[0].Exists);
        Assert.False(string.IsNullOrWhiteSpace(typed.Data.Files[0].Sha256));
        Assert.False(typed.Data.Files[1].Exists);
        Assert.Equal(string.Empty, typed.Data.Files[1].Sha256);
    }

    [Fact]
    public async Task WorkspaceApply_SupportsDryRunCreateAndStaleCasWithoutSideEffects()
    {
        using var project = new TemporaryProjectDirectory();
        var handler = new WorkspaceApplyHandler();
        var context = TestContext.ForProject(project.ProjectPath);
        var path = Path.Combine(project.ProjectPath, "Notes", "new.txt");

        var dryRunResult = await handler.ExecuteAsync(
            Request(
                "workspace.apply",
                "{\"relativePath\":\"Notes/new.txt\",\"content\":\"hello\",\"createIfMissing\":true,\"dryRun\":true}",
                project.ProjectPath),
            context,
            CancellationToken.None);
        var dryRun = Assert.IsType<OperationResult<WorkspaceChangeResult>>(dryRunResult);
        Assert.Equal(OperationStatus.Completed, dryRun.Status);
        Assert.True(dryRun.Data.DryRun);
        Assert.True(dryRun.Data.Created);
        Assert.True(dryRun.Data.Changed);
        Assert.False(File.Exists(path));

        var createResult = await handler.ExecuteAsync(
            Request(
                "workspace.apply",
                "{\"relativePath\":\"Notes/new.txt\",\"content\":\"hello\",\"createIfMissing\":true}",
                project.ProjectPath),
            context,
            CancellationToken.None);
        var created = Assert.IsType<OperationResult<WorkspaceChangeResult>>(createResult);
        Assert.Equal(OperationStatus.Completed, created.Status);
        Assert.True(created.Data.Created);
        Assert.Equal("hello", await File.ReadAllTextAsync(path));

        var staleResult = await handler.ExecuteAsync(
            Request(
                "workspace.apply",
                "{\"relativePath\":\"Notes/new.txt\",\"content\":\"should-not-write\",\"expectedSha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"}",
                project.ProjectPath),
            context,
            CancellationToken.None);
        var stale = Assert.IsType<OperationResult<WorkspaceChangeResult>>(staleResult);
        Assert.Equal(OperationStatus.Failed, stale.Status);
        Assert.Equal(ErrorCodes.PreconditionFailed, stale.Errors[0].Code);
        Assert.Equal("hello", await File.ReadAllTextAsync(path));
    }

    /// <summary>
    /// schema 声明为必填的字段，Handler 必须在调用 Provider 前拒绝其缺失；空对象操作
    /// 也不能静默接受未知字段。测试注入会在被调用时抛异常的 Kernel/Probe，
    /// 因此只要结果仍是 INVALID_ARGUMENT，就能证明失败发生在输入校验阶段。
    /// </summary>
    [Theory]
    [InlineData("operations.describe", "{}")]
    [InlineData("workspace.read", "{}")]
    [InlineData("workspace.search", "{}")]
    [InlineData("workspace.fingerprint", "{}")]
    [InlineData("workspace.fingerprint", "{\"relativePaths\":null}")]
    [InlineData("workspace.fingerprint", "{\"relativePaths\":\"Assets/a.txt\"}")]
    [InlineData("workspace.apply", "{}")]
    [InlineData("workspace.apply", "{\"relativePath\":\"Notes/a.txt\"}")]
    [InlineData("workspace.apply", "{\"relativePath\":\"Notes/a.txt\",\"content\":null}")]
    [InlineData("workspace.read", "{\"relativePath\":\"Assets/a.txt\",\"projectPath\":\"C:\\\\other\"}")]
    [InlineData("workspace.apply", "{\"relativePath\":\"Notes/a.txt\",\"content\":\"x\",\"unknown\":1}")]
    [InlineData("system.doctor", "{\"x\":1}")]
    public async Task InvalidOrDriftedInput_ReturnsInvalidArgumentBeforeProvider(
        string operationId,
        string inputJson)
    {
        IOperationHandler handler = operationId switch
        {
            "operations.describe" => new OperationsDescribeHandler(Catalog()),
            "system.doctor" => new DoctorHandler(UnexpectedProbe.Instance, Catalog(), "0.1.0"),
            "workspace.read" => new WorkspaceReadHandler(),
            "workspace.search" => new WorkspaceSearchHandler(),
            "workspace.fingerprint" => new WorkspaceFingerprintHandler(),
            "workspace.apply" => new WorkspaceApplyHandler(),
            _ => throw new ArgumentOutOfRangeException(nameof(operationId), operationId, "未知测试操作。")
        };

        var result = await handler.ExecuteAsync(
            Request(operationId, inputJson),
            new TestContext(
                new ThrowingWorkspaceKernel(new InvalidOperationException("输入校验失败时不得调用 Provider。")),
                new EmptyCapabilities()),
            CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.InvalidArgument, result.Errors[0].Code);
        Assert.Equal(CliExitCode.InvalidArgument, ExitCodePolicy.Map(result));
    }

    /// <summary>
    /// 缺失 content 必须留在输入校验阶段，目标文件保持原样；只有显式 content="" 才表示清空。
    /// 覆盖已有文件仍需满足 Provider 的 CAS 前置条件，所以显式清空要带上旧内容指纹。
    /// </summary>
    [Fact]
    public async Task WorkspaceApply_MissingContentDoesNotTruncateAndExplicitEmptyClears()
    {
        using var project = new TemporaryProjectDirectory();
        Directory.CreateDirectory(Path.Combine(project.ProjectPath, "Notes"));
        var path = Path.Combine(project.ProjectPath, "Notes", "existing.txt");
        await File.WriteAllTextAsync(path, "keep-me");
        var expectedSha256 = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("keep-me"))).ToLowerInvariant();

        var handler = new WorkspaceApplyHandler();
        var context = TestContext.ForProject(project.ProjectPath);

        var missing = await handler.ExecuteAsync(
            Request("workspace.apply", "{\"relativePath\":\"Notes/existing.txt\"}", project.ProjectPath),
            context,
            CancellationToken.None);
        var missingTyped = Assert.IsType<OperationResult<WorkspaceChangeResult>>(missing);
        Assert.Equal(OperationStatus.Failed, missingTyped.Status);
        Assert.Equal(ErrorCodes.InvalidArgument, missingTyped.Errors[0].Code);
        Assert.Equal(CliExitCode.InvalidArgument, ExitCodePolicy.Map(missingTyped));
        Assert.Equal("keep-me", await File.ReadAllTextAsync(path));

        var clearInput = "{\"relativePath\":\"Notes/existing.txt\",\"content\":\"\",\"expectedSha256\":\""
            + expectedSha256 + "\"}";
        var clear = await handler.ExecuteAsync(
            Request("workspace.apply", clearInput, project.ProjectPath),
            context,
            CancellationToken.None);
        var clearTyped = Assert.IsType<OperationResult<WorkspaceChangeResult>>(clear);
        Assert.Equal(OperationStatus.Completed, clearTyped.Status);
        Assert.True(clearTyped.Data.Changed);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(path));
    }

    /// <summary>relativePaths 显式空数组合法；缺失仍必须是 INVALID_ARGUMENT。</summary>
    [Fact]
    public async Task WorkspaceFingerprint_EmptyArrayIsAcceptedButMissingIsRejected()
    {
        using var project = new TemporaryProjectDirectory();
        var handler = new WorkspaceFingerprintHandler();
        var context = TestContext.ForProject(project.ProjectPath);

        var emptyResult = await handler.ExecuteAsync(
            Request("workspace.fingerprint", "{\"relativePaths\":[]}", project.ProjectPath),
            context,
            CancellationToken.None);
        var empty = Assert.IsType<OperationResult<WorkspaceFingerprintResult>>(emptyResult);
        Assert.Equal(OperationStatus.Completed, empty.Status);
        Assert.Empty(empty.Data.Files);

        var missingResult = await handler.ExecuteAsync(
            Request("workspace.fingerprint", "{}", project.ProjectPath),
            context,
            CancellationToken.None);
        var missing = Assert.IsType<OperationResult<WorkspaceFingerprintResult>>(missingResult);
        Assert.Equal(ErrorCodes.InvalidArgument, missing.Errors[0].Code);
        Assert.Equal(CliExitCode.InvalidArgument, ExitCodePolicy.Map(missing));
    }

    [Fact]
    public async Task WorkspaceRead_PreservesProviderErrorMessageAndDetails()
    {
        var exception = new WorkspaceException(
            ErrorCodes.NotFound,
            "目标文件不存在。",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["relativePath"] = "missing.txt"
            });
        var result = await new WorkspaceReadHandler().ExecuteAsync(
            Request("workspace.read", "{\"relativePath\":\"missing.txt\"}"),
            new TestContext(new ThrowingWorkspaceKernel(exception), new EmptyCapabilities()),
            CancellationToken.None);

        var typed = Assert.IsType<OperationResult<WorkspaceReadResult>>(result);
        Assert.Equal(OperationStatus.Failed, typed.Status);
        Assert.Equal(ErrorCodes.NotFound, typed.Errors[0].Code);
        Assert.Equal("missing.txt", typed.Errors[0].Details["relativePath"]);
        Assert.Equal(CliExitCode.InvalidArgument, ExitCodePolicy.Map(typed));
    }

    [Fact]
    public async Task ProjectContext_WorksWithoutUnityAndReadsProjectVersion()
    {
        using var project = new TemporaryProjectDirectory();
        var handler = new ProjectContextHandler();

        var noUnity = await handler.ExecuteAsync(
            Request("project.context", "{}", project.ProjectPath),
            TestContext.ForProject(project.ProjectPath),
            CancellationToken.None);
        var plain = Assert.IsType<OperationResult<ProjectContextData>>(noUnity);
        Assert.Equal(OperationStatus.Completed, plain.Status);
        Assert.True(plain.Data.ProjectExists);
        Assert.Null(plain.Data.UnityVersion);
        Assert.False(plain.Data.Editor.Connected);
        Assert.Equal("not-connected", plain.Data.Editor.Status);
        Assert.Equal("local", Assert.Single(plain.Data.Providers).Id);

        Directory.CreateDirectory(Path.Combine(project.ProjectPath, "ProjectSettings"));
        await File.WriteAllTextAsync(
            Path.Combine(project.ProjectPath, "ProjectSettings", "ProjectVersion.txt"),
            "m_EditorVersion: 2022.3.10f1\n");
        var withVersion = await handler.ExecuteAsync(
            Request("project.context", "{}", project.ProjectPath),
            TestContext.ForProject(project.ProjectPath),
            CancellationToken.None);
        var versioned = Assert.IsType<OperationResult<ProjectContextData>>(withVersion);
        Assert.Equal("2022.3.10f1", versioned.Data.UnityVersion);
    }

    [Fact]
    public async Task ProjectContext_MissingProjectReturnsProjectNotFound()
    {
        using var project = new TemporaryProjectDirectory();
        var missing = Path.Combine(project.ProjectPath, "does-not-exist");

        var result = await new ProjectContextHandler().ExecuteAsync(
            Request("project.context", "{}", missing),
            TestContext.ForProject(missing),
            CancellationToken.None);

        var typed = Assert.IsType<OperationResult<ProjectContextData>>(result);
        Assert.Equal(OperationStatus.Failed, typed.Status);
        Assert.Equal(ErrorCodes.ProjectNotFound, typed.Errors[0].Code);
        Assert.Equal(CliExitCode.EnvironmentNotReady, ExitCodePolicy.Map(typed));
    }

    private static OperationRequest Request(string operationId, string inputJson, string projectPath = @"C:\project") =>
        new OperationRequest
        {
            OperationId = operationId,
            ProjectPath = projectPath,
            InputJson = inputJson
        };

    private sealed class TestContext : IOperationContext
    {
        public TestContext(IWorkspaceKernel workspace, ICapabilityInspector capabilities)
        {
            Workspace = workspace;
            Capabilities = capabilities;
        }

        public IWorkspaceKernel Workspace { get; }

        public IEditorKernel Editor { get; } = new UnavailableEditor();

        public IArtifactStore Artifacts { get; } = new UnavailableArtifactStore();

        public ICapabilityInspector Capabilities { get; }

        public static TestContext ForProject(string projectPath) =>
            new TestContext(new LocalWorkspaceKernel(projectPath), new EmptyCapabilities());
    }

    private sealed class EmptyCapabilities : ICapabilityInspector
    {
        public Task<CapabilitySnapshot> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CapabilitySnapshot());
    }

    /// <summary>输入校验必须先于环境探测；被调用即说明 Handler 顺序有缺陷。</summary>
    private sealed class UnexpectedProbe : IDoctorEnvironmentProbe
    {
        public static readonly UnexpectedProbe Instance = new UnexpectedProbe();

        public Task<DoctorEnvironmentSnapshot> ProbeAsync(string projectPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("输入不合法时不得执行环境探测。");
    }

    /// <summary>模拟 Handler 之外的 Provider 异常，验证 Operations 没有折叠错误码。</summary>
    private sealed class ThrowingWorkspaceKernel : IWorkspaceKernel
    {
        private readonly Exception _exception;

        public ThrowingWorkspaceKernel(Exception exception) => _exception = exception;

        public Task<ProviderDescriptor> DescribeAsync(CancellationToken cancellationToken) => throw _exception;

        public Task<WorkspaceReadResult> ReadAsync(WorkspaceReadRequest request, CancellationToken cancellationToken) => throw _exception;

        public Task<WorkspaceSearchResult> SearchAsync(WorkspaceSearchRequest request, CancellationToken cancellationToken) => throw _exception;

        public Task<WorkspaceFingerprintResult> FingerprintAsync(WorkspaceFingerprintRequest request, CancellationToken cancellationToken) => throw _exception;

        public Task<WorkspaceChangeResult> ApplyAsync(WorkspaceChangeRequest request, CancellationToken cancellationToken) => throw _exception;
    }

    private sealed class UnavailableEditor : IEditorKernel
    {
        public Task<ProviderDescriptor> DescribeAsync(CancellationToken cancellationToken) => throw Unavailable();

        public Task<EditorStatus> GetStatusAsync(EditorStatusRequest request, CancellationToken cancellationToken) => throw Unavailable();

        public Task<OperationHandle> SubmitAsync(EditorOperationRequest request, CancellationToken cancellationToken) => throw Unavailable();

        public Task<EditorOperationResult> GetResultAsync(OperationHandle handle, CancellationToken cancellationToken) => throw Unavailable();

        public Task CancelAsync(OperationHandle handle, CancellationToken cancellationToken) => throw Unavailable();

        private static NotSupportedException Unavailable() => new NotSupportedException("M1 不连接 Unity。");
    }

    private sealed class UnavailableArtifactStore : IArtifactStore
    {
        public Task<ArtifactReference> WriteAsync(ArtifactWriteRequest request, CancellationToken cancellationToken) => throw Unavailable();

        public Task<ArtifactContent> ReadAsync(ArtifactReference reference, CancellationToken cancellationToken) => throw Unavailable();

        public Task<bool> ExistsAsync(ArtifactReference reference, CancellationToken cancellationToken) => throw Unavailable();

        private static NotSupportedException Unavailable() => new NotSupportedException("测试未装配 Artifact Store。");
    }
}
