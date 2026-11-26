using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;
using BDTool.Operations.Diagnostics;

namespace BDTool.Tests.Diagnostics;

/// <summary>doctor 的检查项、状态汇总与失败映射；全部使用假探测，不触碰真实 Unity。</summary>
public sealed class DoctorHandlerTests
{
    [Fact]
    public async Task AllChecksPass_ReturnsCompletedReadyReportWithNoUnity()
    {
        var handler = CreateHandler(FullyWritableSnapshot());

        var result = await handler.ExecuteAsync(Request(), StubContext.Instance, CancellationToken.None);

        var typed = Assert.IsType<OperationResult<DoctorReport>>(result);
        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.Equal(DoctorStates.Ready, typed.Data.State);
        Assert.Empty(typed.Data.Providers);
        Assert.False(typed.Data.Unity.Connected);
        Assert.Equal("not-implemented", typed.Data.Unity.Integration);
        Assert.Empty(typed.Errors);

        var unity = Assert.Single(typed.Data.Checks, check => check.Id == "unity.connection");
        Assert.Equal(DoctorCheckStatuses.Skipped, unity.Status);

        foreach (var id in new[] { "app.version", "contract.version", "project.path", "state.root", "activity.root", "catalog.loaded", "providers.empty" })
        {
            var check = Assert.Single(typed.Data.Checks, item => item.Id == id);
            Assert.Equal(DoctorCheckStatuses.Pass, check.Status);
        }
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
    public async Task NonEmptyProviders_ProduceWarnAndDegradedState()
    {
        var providers = new List<ProviderDescriptor>
        {
            new ProviderDescriptor { Id = "test-provider", Kind = ProviderKinds.Workspace }
        };
        var handler = new DoctorHandler(
            new FakeProbe(FullyWritableSnapshot()),
            CatalogWithDoctor(),
            "0.1.0",
            providers: providers);

        var result = await handler.ExecuteAsync(Request(), StubContext.Instance, CancellationToken.None);

        var typed = Assert.IsType<OperationResult<DoctorReport>>(result);
        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.Equal(DoctorStates.Degraded, typed.Data.State);
        Assert.Single(typed.Data.Providers);
        var check = Assert.Single(typed.Data.Checks, item => item.Id == "providers.empty");
        Assert.Equal(DoctorCheckStatuses.Warn, check.Status);
    }

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
