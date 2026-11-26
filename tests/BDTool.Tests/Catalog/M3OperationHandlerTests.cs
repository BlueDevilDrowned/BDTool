using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts;
using BDTool.Contracts.Artifacts;
using BDTool.Contracts.Capabilities;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Projects;
using BDTool.Contracts.Providers;
using BDTool.Contracts.Workspace;
using BDTool.Operations.Projects;
using BDTool.Provider.Local;
using BDTool.Tests.Support;

namespace BDTool.Tests.Catalog;

/// <summary>
/// M3 Prefab Handler 的边界测试：只验证 Operations 层对 Editor/Workspace 端口的调用、
/// 严格输入、CAS、dry-run、指纹证据和 Uncertain 语义，不启动真实 Unity。
/// </summary>
public sealed class M3OperationHandlerTests
{
    [Fact]
    public async Task AssetSnapshot_MapsTypedDataAndSubmitsReadOnlyRequest()
    {
        using var project = new TemporaryProjectDirectory();
        const string assetPath = "Assets/Props/Crate.prefab";
        var editor = new ScriptedEditor
        {
            TerminalResult = Completed(JsonSerializer.Serialize(new
            {
                assetPath,
                exists = true,
                sha256 = "abc123",
                rootObjectName = "Crate",
                childCount = 3,
                unityVersion = "6000.0.1f1"
            }))
        };

        var result = await new ProjectAssetSnapshotHandler().ExecuteAsync(
            Request("project.asset-snapshot", $$"""{"assetPath":"{{assetPath}}"}""", project.ProjectPath),
            new TestContext(project.ProjectPath, editor),
            CancellationToken.None);

        var typed = Assert.IsType<OperationResult<ProjectAssetSnapshotData>>(result);
        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.Equal(project.ProjectPath, typed.Data.ProjectPath);
        Assert.Equal(assetPath, typed.Data.AssetPath);
        Assert.True(typed.Data.Exists);
        Assert.Equal("abc123", typed.Data.Sha256);
        Assert.Equal("Crate", typed.Data.RootObjectName);
        Assert.Equal(3, typed.Data.ChildCount);
        Assert.Equal("6000.0.1f1", typed.Data.UnityVersion);

        var submitted = Assert.Single(editor.Submitted);
        Assert.False(submitted.Mutating);
        Assert.False(submitted.DryRun);
        Assert.Empty(submitted.WriteScope);
        Assert.Null(submitted.ExpectedFingerprint);
        Assert.Equal($$"""{"assetPath":"{{assetPath}}"}""", submitted.InputJson);
    }

    [Fact]
    public async Task AssetSnapshot_RejectsUnknownInputBeforeSubmit()
    {
        using var project = new TemporaryProjectDirectory();
        var editor = new ScriptedEditor
        {
            TerminalResult = Completed("{}")
        };

        var result = await new ProjectAssetSnapshotHandler().ExecuteAsync(
            Request(
                "project.asset-snapshot",
                """{"assetPath":"Assets/Props/Crate.prefab","extra":true}""",
                project.ProjectPath),
            new TestContext(project.ProjectPath, editor),
            CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.InvalidArgument, result.Errors[0].Code);
        Assert.Empty(editor.Submitted);
    }

    [Fact]
    public async Task PrefabSetActive_RejectsExtraFieldBeforeWorkspaceOrEditor()
    {
        using var project = new TemporaryProjectDirectory();
        var editor = new ScriptedEditor();

        var result = await new PrefabSetActiveHandler().ExecuteAsync(
            Request(
                "project.prefab-set-active",
                """{"assetPath":"Assets/Props/Crate.prefab","expectedSha256":"abc","isActive":false,"bulk":true}""",
                project.ProjectPath),
            new TestContext(project.ProjectPath, editor),
            CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.InvalidArgument, result.Errors[0].Code);
        Assert.Empty(editor.Submitted);
    }

    /// <summary>
    /// isActive 是必填布尔字段：缺失或类型错误都必须在读取 Workspace 之前拒绝，
    /// 否则漏传字段会被静默折叠成 false 并写入错误状态。
    /// </summary>
    [Theory]
    [InlineData("""{"assetPath":"Assets/Props/Crate.prefab","expectedSha256":"abc"}""")]
    [InlineData("""{"assetPath":"Assets/Props/Crate.prefab","expectedSha256":"abc","isActive":"false"}""")]
    public async Task PrefabSetActive_RejectsMissingOrNonBooleanIsActiveBeforeSubmit(string inputJson)
    {
        using var project = new TemporaryProjectDirectory();
        var editor = new ScriptedEditor();

        var result = await new PrefabSetActiveHandler().ExecuteAsync(
            Request("project.prefab-set-active", inputJson, project.ProjectPath),
            new TestContext(project.ProjectPath, editor),
            CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.InvalidArgument, result.Errors[0].Code);
        Assert.Contains("isActive", result.Errors[0].Message, StringComparison.Ordinal);
        Assert.Empty(editor.Submitted);
    }

    [Fact]
    public async Task PrefabSetActive_CasMismatchDoesNotSubmitEditor()
    {
        using var project = new TemporaryProjectDirectory();
        const string assetPath = "Assets/Props/Crate.prefab";
        WriteFile(project.ProjectPath, assetPath, "old-prefab");
        var editor = new ScriptedEditor();

        var result = await new PrefabSetActiveHandler().ExecuteAsync(
            Request(
                "project.prefab-set-active",
                $$"""{"assetPath":"{{assetPath}}","expectedSha256":"{{new string('0', 64)}}","isActive":false}""",
                project.ProjectPath),
            new TestContext(project.ProjectPath, editor),
            CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.PreconditionFailed, result.Errors[0].Code);
        Assert.Equal(assetPath, result.Errors[0].Details["assetPath"]);
        Assert.Empty(editor.Submitted);
    }

    [Fact]
    public async Task PrefabSetActive_WriteUsesCasScopeAndReturnsWorkspaceProof()
    {
        using var project = new TemporaryProjectDirectory();
        const string assetPath = "Assets/Props/Crate.prefab";
        var oldBytes = Encoding.UTF8.GetBytes("old-prefab");
        var newBytes = Encoding.UTF8.GetBytes("new-prefab");
        WriteBytes(project.ProjectPath, assetPath, oldBytes);
        var oldSha = Sha256(oldBytes);
        var newSha = Sha256(newBytes);

        var editor = new ScriptedEditor
        {
            AfterSubmit = _ => WriteBytes(project.ProjectPath, assetPath, newBytes),
            TerminalResult = Completed(ActivationJson(
                assetPath,
                oldSha,
                newSha,
                wasActive: true,
                isActive: false,
                changed: true,
                saved: true,
                dryRun: false,
                undoGroup: "PrefabSetActive:1",
                verified: true))
        };

        var result = await new PrefabSetActiveHandler().ExecuteAsync(
            Request(
                "project.prefab-set-active",
                $$"""{"assetPath":"{{assetPath}}","expectedSha256":"{{oldSha}}","isActive":false,"dryRun":false}""",
                project.ProjectPath),
            new TestContext(project.ProjectPath, editor),
            CancellationToken.None);

        var typed = Assert.IsType<OperationResult<PrefabActivationData>>(result);
        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.Equal(oldSha, typed.Data.PreviousSha256);
        Assert.Equal(newSha, typed.Data.NewSha256);
        Assert.True(typed.Data.WasActive);
        Assert.False(typed.Data.IsActive);
        Assert.True(typed.Data.Changed);
        Assert.True(typed.Data.Saved);
        Assert.True(typed.Data.Verified);
        Assert.False(typed.Data.DryRun);
        Assert.Equal("PrefabSetActive:1", typed.Data.UndoGroup);
        Assert.Equal(new[] { assetPath }, typed.ChangedAssets);
        Assert.Equal(oldSha, typed.Proof.InputFingerprint);
        Assert.Equal(newSha, typed.Proof.OutputFingerprint);
        Assert.Equal("false", typed.Proof.Evidence["requestedIsActive"]);
        Assert.Equal("true", typed.Proof.Evidence["saved"]);
        Assert.Empty(typed.Warnings);

        var submitted = Assert.Single(editor.Submitted);
        Assert.Equal("project.prefab-set-active", submitted.OperationId);
        Assert.True(submitted.Mutating);
        Assert.False(submitted.DryRun);
        Assert.Equal(oldSha, submitted.ExpectedFingerprint);
        Assert.Equal(new[] { assetPath }, submitted.WriteScope);
        using var input = JsonDocument.Parse(submitted.InputJson);
        Assert.False(input.RootElement.GetProperty("isActive").GetBoolean());
        Assert.False(input.RootElement.GetProperty("dryRun").GetBoolean());
    }

    [Fact]
    public async Task PrefabSetActive_DryRunKeepsMutatingFlagButDoesNotChangeFile()
    {
        using var project = new TemporaryProjectDirectory();
        const string assetPath = "Assets/Props/Crate.prefab";
        var bytes = Encoding.UTF8.GetBytes("old-prefab");
        WriteBytes(project.ProjectPath, assetPath, bytes);
        var sha = Sha256(bytes);

        var editor = new ScriptedEditor
        {
            TerminalResult = Completed(ActivationJson(
                assetPath,
                sha,
                sha,
                wasActive: true,
                isActive: false,
                changed: true,
                saved: false,
                dryRun: true,
                undoGroup: string.Empty,
                verified: false))
        };

        var result = await new PrefabSetActiveHandler().ExecuteAsync(
            Request(
                "project.prefab-set-active",
                $$"""{"assetPath":"{{assetPath}}","expectedSha256":"{{sha}}","isActive":false,"dryRun":true}""",
                project.ProjectPath),
            new TestContext(project.ProjectPath, editor),
            CancellationToken.None);

        var typed = Assert.IsType<OperationResult<PrefabActivationData>>(result);
        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.True(typed.Data.DryRun);
        Assert.True(typed.Data.WasActive);
        Assert.False(typed.Data.IsActive);
        Assert.True(typed.Data.Changed);
        Assert.False(typed.Data.Saved);
        Assert.False(typed.Data.Verified);
        Assert.Equal(sha, typed.Proof.InputFingerprint);
        Assert.Equal(sha, typed.Proof.OutputFingerprint);
        Assert.Contains(typed.Warnings, warning => warning.Code == "PREFAB_SET_ACTIVE_DRY_RUN");
        Assert.Empty(typed.ChangedAssets);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(project.ProjectPath, assetPath)));

        var submitted = Assert.Single(editor.Submitted);
        Assert.True(submitted.Mutating);
        Assert.True(submitted.DryRun);
        Assert.Equal(new[] { assetPath }, submitted.WriteScope);
    }

    [Fact]
    public async Task PrefabSetActive_PostFingerprintMismatchReturnsExecutionUncertain()
    {
        using var project = new TemporaryProjectDirectory();
        const string assetPath = "Assets/Props/Crate.prefab";
        var bytes = Encoding.UTF8.GetBytes("old-prefab");
        WriteBytes(project.ProjectPath, assetPath, bytes);
        var sha = Sha256(bytes);

        var editor = new ScriptedEditor
        {
            TerminalResult = Completed(ActivationJson(
                assetPath,
                sha,
                Sha256(Encoding.UTF8.GetBytes("reported-but-not-on-disk")),
                wasActive: true,
                isActive: false,
                changed: true,
                saved: true,
                dryRun: false,
                undoGroup: "PrefabSetActive:2",
                verified: true))
        };

        var result = await new PrefabSetActiveHandler().ExecuteAsync(
            Request(
                "project.prefab-set-active",
                $$"""{"assetPath":"{{assetPath}}","expectedSha256":"{{sha}}","isActive":false}""",
                project.ProjectPath),
            new TestContext(project.ProjectPath, editor),
            CancellationToken.None);

        Assert.Equal(OperationStatus.Uncertain, result.Status);
        Assert.Equal(ErrorCodes.ExecutionUncertain, result.Errors[0].Code);
        Assert.Equal("post_fingerprint_mismatch", result.Errors[0].Details["reason"]);
    }

    [Fact]
    public async Task PrefabSetActive_TimeoutAfterSubmitReturnsExecutionUncertain()
    {
        using var project = new TemporaryProjectDirectory();
        const string assetPath = "Assets/Props/Crate.prefab";
        var bytes = Encoding.UTF8.GetBytes("old-prefab");
        WriteBytes(project.ProjectPath, assetPath, bytes);
        var sha = Sha256(bytes);
        var editor = new ScriptedEditor { AlwaysRunning = true };
        var request = Request(
            "project.prefab-set-active",
            $$"""{"assetPath":"{{assetPath}}","expectedSha256":"{{sha}}","isActive":false}""",
            project.ProjectPath);
        request.TimeoutSeconds = 1;

        var result = await new PrefabSetActiveHandler().ExecuteAsync(
            request,
            new TestContext(project.ProjectPath, editor),
            CancellationToken.None);

        Assert.Equal(OperationStatus.Uncertain, result.Status);
        Assert.Equal(ErrorCodes.ExecutionUncertain, result.Errors[0].Code);
        Assert.Equal("write_timeout", result.Errors[0].Details["reason"]);
        Assert.Single(editor.Submitted);
        Assert.Equal(1, editor.CancelCalls);
    }

    [Fact]
    public async Task PrefabSetActive_RealWriteWithUnverifiedEvidenceReturnsExecutionUncertain()
    {
        using var project = new TemporaryProjectDirectory();
        const string assetPath = "Assets/Props/Crate.prefab";
        var bytes = Encoding.UTF8.GetBytes("old-prefab");
        WriteBytes(project.ProjectPath, assetPath, bytes);
        var sha = Sha256(bytes);

        // 三个证据同时不成立：verified=false、回读 isActive 不是请求值、
        // newSha256 未变化。Handler 必须在读取 post-SHA 之前拒绝确认成功。
        var editor = new ScriptedEditor
        {
            TerminalResult = Completed(ActivationJson(
                assetPath,
                sha,
                sha,
                wasActive: false,
                isActive: false,
                changed: true,
                saved: true,
                dryRun: false,
                undoGroup: "PrefabSetActive:3",
                verified: false))
        };

        var result = await new PrefabSetActiveHandler().ExecuteAsync(
            Request(
                "project.prefab-set-active",
                $$"""{"assetPath":"{{assetPath}}","expectedSha256":"{{sha}}","isActive":true}""",
                project.ProjectPath),
            new TestContext(project.ProjectPath, editor),
            CancellationToken.None);

        Assert.Equal(OperationStatus.Uncertain, result.Status);
        Assert.Equal(ErrorCodes.ExecutionUncertain, result.Errors[0].Code);
        Assert.Equal("write_unverified", result.Errors[0].Details["reason"]);
        Assert.Single(editor.Submitted);
    }

    [Fact]
    public async Task PrefabSetActive_RealWriteWithoutChangedReturnsExecutionUncertain()
    {
        using var project = new TemporaryProjectDirectory();
        const string assetPath = "Assets/Props/Crate.prefab";
        var bytes = Encoding.UTF8.GetBytes("old-prefab");
        WriteBytes(project.ProjectPath, assetPath, bytes);
        var sha = Sha256(bytes);

        // 真实写要求文件 SHA 发生变化；Editor 报告 changed=false 时即使 saved/verified
        // 都为 true，也不能把本次请求确认成有效写入。
        var editor = new ScriptedEditor
        {
            TerminalResult = Completed(ActivationJson(
                assetPath,
                sha,
                Sha256(Encoding.UTF8.GetBytes("new-prefab")),
                wasActive: true,
                isActive: false,
                changed: false,
                saved: true,
                dryRun: false,
                undoGroup: "PrefabSetActive:4",
                verified: true))
        };

        var result = await new PrefabSetActiveHandler().ExecuteAsync(
            Request(
                "project.prefab-set-active",
                $$"""{"assetPath":"{{assetPath}}","expectedSha256":"{{sha}}","isActive":false}""",
                project.ProjectPath),
            new TestContext(project.ProjectPath, editor),
            CancellationToken.None);

        Assert.Equal(OperationStatus.Uncertain, result.Status);
        Assert.Equal(ErrorCodes.ExecutionUncertain, result.Errors[0].Code);
        Assert.Equal("write_not_changed", result.Errors[0].Details["reason"]);
    }

    private static OperationRequest Request(string operationId, string inputJson, string projectPath) =>
        new OperationRequest
        {
            OperationId = operationId,
            ProjectPath = projectPath,
            InputJson = inputJson
        };

    private static EditorOperationResult Completed(string outputJson) =>
        new EditorOperationResult
        {
            Status = OperationStatus.Completed,
            OutputJson = outputJson
        };

    private static string ActivationJson(
        string assetPath,
        string previousSha256,
        string newSha256,
        bool wasActive,
        bool isActive,
        bool changed,
        bool saved,
        bool dryRun,
        string undoGroup,
        bool verified) =>
        JsonSerializer.Serialize(new
        {
            assetPath,
            previousSha256,
            newSha256,
            wasActive,
            isActive,
            changed,
            saved,
            dryRun,
            undoGroup,
            verified
        });

    private static void WriteFile(string projectPath, string relativePath, string content) =>
        WriteBytes(projectPath, relativePath, Encoding.UTF8.GetBytes(content));

    private static void WriteBytes(string projectPath, string relativePath, byte[] bytes)
    {
        var path = Path.Combine(projectPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class TestContext : IOperationContext
    {
        public TestContext(string projectPath, IEditorKernel editor)
        {
            Workspace = new LocalWorkspaceKernel(projectPath);
            Editor = editor;
        }

        public IWorkspaceKernel Workspace { get; }
        public IEditorKernel Editor { get; }
        public IArtifactStore Artifacts { get; } = new UnavailableArtifactStore();
        public ICapabilityInspector Capabilities { get; } = new EmptyCapabilities();
    }

    private sealed class EmptyCapabilities : ICapabilityInspector
    {
        public Task<CapabilitySnapshot> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CapabilitySnapshot());
    }

    private sealed class UnavailableArtifactStore : IArtifactStore
    {
        public Task<ArtifactReference> WriteAsync(ArtifactWriteRequest request, CancellationToken cancellationToken) =>
            throw Unavailable();

        public Task<ArtifactContent> ReadAsync(ArtifactReference reference, CancellationToken cancellationToken) =>
            throw Unavailable();

        public Task<bool> ExistsAsync(ArtifactReference reference, CancellationToken cancellationToken) =>
            throw Unavailable();

        private static NotSupportedException Unavailable() =>
            new NotSupportedException("测试未装配 Artifact Store。");
    }

    /// <summary>
    /// 可编排的 Editor 端口。Submit 会记录请求并按需修改磁盘文件；GetResult 可一直返回 Running
    /// 以验证提交后的超时 Uncertain，而不是启动真实 Unity 进程。
    /// </summary>
    private sealed class ScriptedEditor : IEditorKernel
    {
        private int _nextJob;

        public List<EditorOperationRequest> Submitted { get; } = new List<EditorOperationRequest>();
        public EditorOperationResult TerminalResult { get; set; } = Completed("{}");
        public bool AlwaysRunning { get; set; }
        public Action<EditorOperationRequest>? AfterSubmit { get; set; }
        public int CancelCalls { get; private set; }

        public Task<ProviderDescriptor> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderDescriptor { Id = "test", Kind = ProviderKinds.Editor });

        public Task<EditorStatus> GetStatusAsync(EditorStatusRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new EditorStatus { State = "ready" });

        public Task<OperationHandle> SubmitAsync(
            EditorOperationRequest request,
            CancellationToken cancellationToken)
        {
            Submitted.Add(request);
            AfterSubmit?.Invoke(request);
            _nextJob++;
            return Task.FromResult(new OperationHandle
            {
                JobId = "job-" + _nextJob,
                OperationId = request.OperationId
            });
        }

        public Task<EditorOperationResult> GetResultAsync(
            OperationHandle handle,
            CancellationToken cancellationToken) =>
            Task.FromResult(AlwaysRunning
                ? new EditorOperationResult { Handle = handle, Status = OperationStatus.Running }
                : TerminalResult);

        public Task CancelAsync(OperationHandle handle, CancellationToken cancellationToken)
        {
            CancelCalls++;
            return Task.CompletedTask;
        }
    }
}
