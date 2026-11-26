using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;
using BDTool.Operations.Diagnostics;

namespace BDTool.Tests.Diagnostics;

/// <summary>doctor 的检查项、状态汇总与失败映射；全部使用假探测，不触碰真实 Unity。</summary>
public sealed class DoctorHandlerTests
{
    [Fact]
    public async Task WithoutProviders_ReturnsDegradedReportWithNoUnity()
    {
        var handler = CreateHandler(FullyWritableSnapshot());

        var result = await handler.ExecuteAsync(Request(), StubContext.Instance, CancellationToken.None);

        var typed = Assert.IsType<OperationResult<DoctorReport>>(result);
        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.Equal(DoctorStates.Degraded, typed.Data.State);
        Assert.Equal("M6", typed.Data.Milestone);
        Assert.Empty(typed.Data.Providers);
        Assert.False(typed.Data.Unity.Connected);
        Assert.Equal("not-assembled", typed.Data.Unity.Integration);
        Assert.Empty(typed.Errors);

        var unity = Assert.Single(typed.Data.Checks, check => check.Id == "unity.connection");
        Assert.Equal(DoctorCheckStatuses.Skipped, unity.Status);

        foreach (var id in new[] { "app.version", "contract.version", "project.path", "state.root", "activity.root", "catalog.loaded" })
        {
            var check = Assert.Single(typed.Data.Checks, item => item.Id == id);
            Assert.Equal(DoctorCheckStatuses.Pass, check.Status);
        }

        // 没有 Provider 时 doctor 必须降级，不能把空快照报告成 ready。
        var providers = Assert.Single(typed.Data.Checks, item => item.Id == "providers.available");
        Assert.Equal(DoctorCheckStatuses.Warn, providers.Status);
    }

    [Fact]
    public async Task MissingProject_ReturnsProjectNotFound()
    {
        var snapshot = FullyWritableSnapshot();
        snapshot.ProjectExists = false;
        snapshot.ProjectDetail = "项目目录不存在。";
        var handler = CreateHandler(snapshot);

        var result = await handler.ExecuteAsync(Request(), StubContext.Instance, CancellationToken.None);

        var typed = Assert.IsType<OperationResult<DoctorReport>>(result);
        Assert.Equal(OperationStatus.Failed, typed.Status);
        Assert.Equal(DoctorStates.NotReady, typed.Data.State);
        Assert.Equal(ErrorCodes.ProjectNotFound, typed.Errors[0].Code);
        Assert.Equal("project.path", typed.Errors[0].Details["checkId"]);
    }

    [Fact]
    public async Task UnwritableActivityRoot_ReturnsEnvironmentUnsupported()
    {
        var snapshot = FullyWritableSnapshot();
        snapshot.ActivityRootWritable = false;
        snapshot.ActivityRootDetail = "不可写：UnauthorizedAccessException";
        var handler = CreateHandler(snapshot);

        var result = await handler.ExecuteAsync(Request(), StubContext.Instance, CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.EnvironmentUnsupported, result.Errors[0].Code);
        var typed = Assert.IsType<OperationResult<DoctorReport>>(result);
        var check = Assert.Single(typed.Data.Checks, item => item.Id == "activity.root");
        Assert.Equal(DoctorCheckStatuses.Fail, check.Status);
    }

    [Fact]
    public async Task EmptyRequestProject_UsesCompositionDefaultProject()
    {
        var probe = new RecordingProbe(FullyWritableSnapshot());
        var handler = new DoctorHandler(probe, CatalogWithDoctor(), "0.1.0", @"C:\default-project");

        await handler.ExecuteAsync(
            new OperationRequest { OperationId = DoctorHandler.StableOperationId },
            StubContext.Instance,
            CancellationToken.None);

        Assert.Equal(@"C:\default-project", probe.LastProjectPath);
    }

    [Fact]
    public async Task LocalWorkspaceProvider_ReturnsReadyStateAndProviderCheckPasses()
    {
        var typed = await RunWithProvidersAsync(ValidWorkspaceProvider());

        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.Equal(DoctorStates.Ready, typed.Data.State);
        Assert.Empty(typed.Errors);
        var provider = Assert.Single(typed.Data.Providers);
        Assert.Equal("local", provider.Id);

        var check = Assert.Single(typed.Data.Checks, item => item.Id == "providers.available");
        Assert.Equal(DoctorCheckStatuses.Pass, check.Status);
        Assert.Contains("provider[0]", check.Detail, StringComparison.Ordinal);
        Assert.Contains("contract=" + BDToolContract.Version, check.Detail, StringComparison.Ordinal);
        Assert.Contains("workspace.fingerprint=true", check.Detail, StringComparison.Ordinal);

        // Provider 通过不等于 Unity 可用：M1 必须保持 skipped 且 connected=false。
        Assert.False(typed.Data.Unity.Connected);
        var unity = Assert.Single(typed.Data.Checks, item => item.Id == "unity.connection");
        Assert.Equal(DoctorCheckStatuses.Skipped, unity.Status);
    }

    [Fact]
    public async Task EditorReadyForRequestedProject_PassesUnityConnection()
    {
        var typed = await RunWithEditorAsync(new EditorStatus
        {
            State = "ready",
            ProjectPath = @"C:\project",
            UnityVersion = "6000.0.0f1"
        });

        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.Equal(DoctorStates.Ready, typed.Data.State);
        Assert.True(typed.Data.Unity.Connected);
        Assert.Equal("ready", typed.Data.Unity.Integration);
        var check = Assert.Single(typed.Data.Checks, item => item.Id == "unity.connection");
        Assert.Equal(DoctorCheckStatuses.Pass, check.Status);
    }

    [Theory]
    [InlineData(@"C:\other-project")]
    [InlineData("")]
    public async Task EditorReadyForUnconfirmedProject_WarnsAndStaysDisconnected(string reportedProjectPath)
    {
        var typed = await RunWithEditorAsync(new EditorStatus
        {
            State = "ready",
            ProjectPath = reportedProjectPath
        });

        Assert.Equal(DoctorStates.Degraded, typed.Data.State);
        Assert.False(typed.Data.Unity.Connected);
        var check = Assert.Single(typed.Data.Checks, item => item.Id == "unity.connection");
        Assert.Equal(DoctorCheckStatuses.Warn, check.Status);
        Assert.Contains(
            string.IsNullOrWhiteSpace(reportedProjectPath) ? "projectPath=<empty>" : "不一致",
            check.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditorTimeout_MapsToStableTimeoutStateAndFails()
    {
        var typed = await RunWithEditorAsync(exception: new TimeoutException("状态查询超时。"));

        Assert.Equal(OperationStatus.Failed, typed.Status);
        Assert.Equal(DoctorStates.NotReady, typed.Data.State);
        Assert.False(typed.Data.Unity.Connected);
        Assert.Equal("timeout", typed.Data.Unity.Integration);
        var check = Assert.Single(typed.Data.Checks, item => item.Id == "unity.connection");
        Assert.Equal(DoctorCheckStatuses.Fail, check.Status);
        Assert.Equal(ErrorCodes.EnvironmentUnsupported, Assert.Single(typed.Errors).Code);
    }

    [Fact]
    public async Task EditorException_MapsToStableErrorStateAndFails()
    {
        var typed = await RunWithEditorAsync(
            exception: new EditorException(ErrorCodes.PreconditionFailed, "项目不匹配。"));

        Assert.Equal(OperationStatus.Failed, typed.Status);
        Assert.Equal(DoctorStates.NotReady, typed.Data.State);
        Assert.False(typed.Data.Unity.Connected);
        Assert.Equal("error", typed.Data.Unity.Integration);
        Assert.Contains(ErrorCodes.PreconditionFailed, typed.Data.Unity.Detail, StringComparison.Ordinal);
        var check = Assert.Single(typed.Data.Checks, item => item.Id == "unity.connection");
        Assert.Equal(DoctorCheckStatuses.Fail, check.Status);
    }

    [Fact]
    public async Task ProviderWithEmptyId_FailsWithProviderIdEmptyReason()
    {
        var provider = ValidWorkspaceProvider();
        provider.Id = string.Empty;

        var typed = await RunWithProvidersAsync(provider);

        var check = AssertProviderFailure(typed, "PROVIDER_ID_EMPTY");
        Assert.Contains("id=;", check.Detail, StringComparison.Ordinal);
        var error = Assert.Single(typed.Errors);
        Assert.Equal("0", error.Details["providerIndex"]);
        Assert.Equal(BDToolContract.Version, error.Details["expectedContractVersion"]);
    }

    [Fact]
    public async Task ProviderWithUnknownKind_FailsWithKindInvalidReason()
    {
        var provider = ValidWorkspaceProvider();
        provider.Kind = "editorr";

        var typed = await RunWithProvidersAsync(provider);

        var check = AssertProviderFailure(typed, "PROVIDER_KIND_INVALID");
        Assert.Contains("kind=editorr", check.Detail, StringComparison.Ordinal);
        Assert.Contains("kindValid=false", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderWithContractMismatch_FailsWithActualContractVersion()
    {
        var provider = ValidWorkspaceProvider();
        provider.ContractVersion = "0.0.9";

        var typed = await RunWithProvidersAsync(provider);

        AssertProviderFailure(typed, "PROVIDER_CONTRACT_MISMATCH");
        var error = Assert.Single(typed.Errors);
        Assert.Equal(BDToolContract.Version, error.Details["expectedContractVersion"]);
        Assert.Equal("0.0.9", error.Details["actualContractVersion"]);
    }

    [Fact]
    public async Task ProviderWithEmptyImplementationVersion_FailsWithStableReason()
    {
        var provider = ValidWorkspaceProvider();
        provider.ImplementationVersion = " ";

        var typed = await RunWithProvidersAsync(provider);

        AssertProviderFailure(typed, "PROVIDER_IMPLEMENTATION_VERSION_EMPTY");
    }

    [Fact]
    public async Task ProviderMissingWorkspaceCapability_FailsAndReportsMissingCapability()
    {
        var provider = ValidWorkspaceProvider();
        provider.Capabilities.Remove(CapabilityIds.WorkspaceSearch);

        var typed = await RunWithProvidersAsync(provider);

        var check = AssertProviderFailure(typed, "PROVIDER_CAPABILITIES_MISSING");
        Assert.Contains("workspace.search=false", check.Detail, StringComparison.Ordinal);
        Assert.Contains("missing=workspace.search", check.Detail, StringComparison.Ordinal);
        var error = Assert.Single(typed.Errors);
        Assert.Equal("workspace.search", error.Details["missingCapabilities"]);
    }

    [Fact]
    public async Task ProviderWithNullCapabilities_FailsForAllFourCapabilities()
    {
        var provider = ValidWorkspaceProvider();
        provider.Capabilities = null!;

        var typed = await RunWithProvidersAsync(provider);

        var check = AssertProviderFailure(typed, "PROVIDER_CAPABILITIES_MISSING");
        Assert.Contains("capabilities=<null>", check.Detail, StringComparison.Ordinal);
        var error = Assert.Single(typed.Errors);
        Assert.Equal(
            "workspace.read,workspace.write,workspace.search,workspace.fingerprint",
            error.Details["missingCapabilities"]);
    }

    [Fact]
    public async Task ProviderWithEmptyDisplayName_DegradesInsteadOfFailing()
    {
        var provider = ValidWorkspaceProvider();
        provider.DisplayName = string.Empty;

        var typed = await RunWithProvidersAsync(provider);

        // 展示名不影响能力判断，但描述不完整必须可见，因此降级而不是 ready/failed。
        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.Equal(DoctorStates.Degraded, typed.Data.State);
        Assert.Empty(typed.Errors);
        var check = Assert.Single(typed.Data.Checks, item => item.Id == "providers.available");
        Assert.Equal(DoctorCheckStatuses.Warn, check.Status);
        Assert.Contains("PROVIDER_DISPLAY_NAME_EMPTY", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditorOnlyProvider_FailsBecauseWorkspaceProviderIsMissing()
    {
        var editor = new ProviderDescriptor
        {
            Id = "editor",
            DisplayName = "Editor",
            Kind = ProviderKinds.Editor,
            ContractVersion = BDToolContract.Version,
            ImplementationVersion = "0.1.0"
        };

        var typed = await RunWithProvidersAsync(editor);

        var check = AssertProviderFailure(typed, "PROVIDER_WORKSPACE_MISSING");
        Assert.Contains("workspaceProvider=none", check.Detail, StringComparison.Ordinal);
        var error = Assert.Single(typed.Errors);
        Assert.Equal("-1", error.Details["providerIndex"]);
    }

    [Fact]
    public async Task DuplicateProviderIds_FailInsteadOfSilentlyOverwriting()
    {
        var typed = await RunWithProvidersAsync(ValidWorkspaceProvider(), ValidWorkspaceProvider());

        AssertProviderFailure(typed, "PROVIDER_ID_DUPLICATE");
        var error = Assert.Single(typed.Errors);
        Assert.Equal("1", error.Details["providerIndex"]);
        Assert.Equal("2", error.Details["providerCount"]);
    }

    /// <summary>统一断言 Provider 检查失败：结果 Failed、报告 not-ready、错误码与原因稳定。</summary>
    private static DoctorCheck AssertProviderFailure(OperationResult<DoctorReport> typed, string reason)
    {
        Assert.Equal(OperationStatus.Failed, typed.Status);
        Assert.Equal(DoctorStates.NotReady, typed.Data.State);

        var check = Assert.Single(typed.Data.Checks, item => item.Id == "providers.available");
        Assert.Equal(DoctorCheckStatuses.Fail, check.Status);
        Assert.Contains(reason, check.Detail, StringComparison.Ordinal);

        var error = Assert.Single(typed.Errors);
        Assert.Equal(ErrorCodes.EnvironmentUnsupported, error.Code);
        Assert.Equal("providers.available", error.Details["checkId"]);
        Assert.Equal(reason, error.Details["reason"]);
        return check;
    }

    private static async Task<OperationResult<DoctorReport>> RunWithProvidersAsync(
        params ProviderDescriptor[] providers)
    {
        var handler = new DoctorHandler(
            new FakeProbe(FullyWritableSnapshot()),
            CatalogWithDoctor(),
            "0.1.0",
            providers: providers);

        var result = await handler.ExecuteAsync(Request(), StubContext.Instance, CancellationToken.None);
        return Assert.IsType<OperationResult<DoctorReport>>(result);
    }

    private static async Task<OperationResult<DoctorReport>> RunWithEditorAsync(
        EditorStatus? status = null,
        Exception? exception = null)
    {
        var handler = new DoctorHandler(
            new FakeProbe(FullyWritableSnapshot()),
            CatalogWithDoctor(),
            "0.1.0",
            @"C:\project",
            new[] { ValidWorkspaceProvider() },
            new FakeEditor(status, exception));

        var result = await handler.ExecuteAsync(Request(), StubContext.Instance, CancellationToken.None);
        return Assert.IsType<OperationResult<DoctorReport>>(result);
    }

    private static ProviderDescriptor ValidWorkspaceProvider() => new ProviderDescriptor
    {
        Id = "local",
        DisplayName = "Local Workspace",
        Kind = ProviderKinds.Workspace,
        ContractVersion = BDToolContract.Version,
        ImplementationVersion = "0.1.0",
        Capabilities = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [CapabilityIds.WorkspaceRead] = true,
            [CapabilityIds.WorkspaceWrite] = true,
            [CapabilityIds.WorkspaceSearch] = true,
            [CapabilityIds.WorkspaceFingerprint] = true
        }
    };

    private static DoctorHandler CreateHandler(DoctorEnvironmentSnapshot snapshot) =>
        new DoctorHandler(new FakeProbe(snapshot), CatalogWithDoctor(), "0.1.0", @"C:\project");

    private static DoctorEnvironmentSnapshot FullyWritableSnapshot() => new DoctorEnvironmentSnapshot
    {
        ProjectPath = @"C:\project",
        ProjectExists = true,
        ProjectDetail = "项目目录存在。",
        StateRoot = @"C:\project\.bdtool",
        StateRootWritable = true,
        StateRootDetail = "目录可创建并可写。",
        ActivityRoot = @"C:\project\.bdtool\state\activity",
        ActivityRootWritable = true,
        ActivityRootDetail = "目录可创建并可写。"
    };

    private static OperationRequest Request() => new OperationRequest
    {
        OperationId = DoctorHandler.StableOperationId,
        ProjectPath = @"C:\project"
    };

    private static IOperationCatalog CatalogWithDoctor() => new StubCatalog(new OperationDescriptor
    {
        Id = DoctorHandler.StableOperationId,
        Description = "doctor",
        Effect = OperationEffects.Read,
        Environment = new List<string> { OperationEnvironments.Workspace },
        Implemented = true,
        Availability = OperationAvailability.Available,
        Milestone = "M0"
    });

    private sealed class FakeProbe : IDoctorEnvironmentProbe
    {
        private readonly DoctorEnvironmentSnapshot _snapshot;

        public FakeProbe(DoctorEnvironmentSnapshot snapshot) => _snapshot = snapshot;

        public Task<DoctorEnvironmentSnapshot> ProbeAsync(string projectPath, CancellationToken cancellationToken) =>
            Task.FromResult(_snapshot);
    }

    /// <summary>用固定状态或异常模拟 Editor Provider，不启动真实 Unity。</summary>
    private sealed class FakeEditor : IEditorKernel
    {
        private readonly EditorStatus? _status;
        private readonly Exception? _exception;

        public FakeEditor(EditorStatus? status, Exception? exception)
        {
            _status = status;
            _exception = exception;
        }

        public Task<ProviderDescriptor> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderDescriptor
            {
                Id = "akio",
                DisplayName = "Akio",
                Kind = ProviderKinds.Editor,
                ContractVersion = BDToolContract.Version,
                ImplementationVersion = "0.2.0"
            });

        public Task<EditorStatus> GetStatusAsync(
            EditorStatusRequest request,
            CancellationToken cancellationToken) =>
            _exception is null
                ? Task.FromResult(_status ?? throw new InvalidOperationException("测试未提供 Editor 状态。"))
                : Task.FromException<EditorStatus>(_exception);

        public Task<OperationHandle> SubmitAsync(
            EditorOperationRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EditorOperationResult> GetResultAsync(
            OperationHandle handle,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CancelAsync(OperationHandle handle, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingProbe : IDoctorEnvironmentProbe
    {
        private readonly DoctorEnvironmentSnapshot _snapshot;

        public RecordingProbe(DoctorEnvironmentSnapshot snapshot) => _snapshot = snapshot;

        public string LastProjectPath { get; private set; } = string.Empty;

        public Task<DoctorEnvironmentSnapshot> ProbeAsync(string projectPath, CancellationToken cancellationToken)
        {
            LastProjectPath = projectPath;
            return Task.FromResult(_snapshot);
        }
    }

    private sealed class StubCatalog : IOperationCatalog
    {
        private readonly List<OperationDescriptor> _items;

        public StubCatalog(OperationDescriptor descriptor) => _items = new List<OperationDescriptor> { descriptor };

        public IReadOnlyList<OperationDescriptor> GetAll() => _items;

        public OperationDescriptor? Find(string? operationId) =>
            _items.Find(item => string.Equals(item.Id, operationId, StringComparison.Ordinal));
    }

    private sealed class StubContext : IOperationContext
    {
        public static readonly StubContext Instance = new StubContext();

        public Contracts.Workspace.IWorkspaceKernel Workspace => throw new NotSupportedException();

        public Contracts.Editor.IEditorKernel Editor => throw new NotSupportedException();

        public Contracts.Artifacts.IArtifactStore Artifacts => throw new NotSupportedException();

        public Contracts.Capabilities.ICapabilityInspector Capabilities => throw new NotSupportedException();
    }
}
