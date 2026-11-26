using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Application;
using BDTool.Contracts;
using BDTool.Contracts.Activity;
using BDTool.Contracts.Artifacts;
using BDTool.Contracts.Capabilities;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Gateway;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Workspace;
using BDTool.Operations.Handlers;

namespace BDTool.Tests.Application;

/// <summary>网关在 M0 的失败语义、分发路径、审计与退出码。</summary>
public sealed class ApplicationGatewayTests
{
    [Fact]
    public async Task UnknownOperation_ReturnsNotFoundAndExitCodeTwo()
    {
        var gateway = CreateGateway(Descriptor("system.doctor"));

        var result = await gateway.ExecuteAsync(
            Request("missing.operation"),
            Activity(),
            CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.NotFound, result.Errors[0].Code);
        Assert.Equal(CliExitCode.InvalidArgument, ExitCodePolicy.Map(result));
    }

    [Fact]
    public async Task UnregisteredOperation_ReturnsStructuredUnsupported()
    {
        var gateway = CreateGateway(Descriptor("system.doctor"));

        var result = await gateway.ExecuteAsync(
            Request("system.doctor"),
            Activity(),
            CancellationToken.None);

        Assert.Equal(OperationStatus.Unsupported, result.Status);
        Assert.Equal(ErrorCodes.CapabilityUnsupported, result.Errors[0].Code);
        Assert.Equal("system.doctor", result.Errors[0].Details["operationId"]);
        Assert.Equal(CliExitCode.CapabilityUnsupported, ExitCodePolicy.Map(result));
    }

    [Fact]
    public async Task MissingRuntimeContext_IsNotReportedAsSuccess()
    {
        var descriptor = Descriptor("system.doctor", implemented: true);
        var registry = new OperationHandlerRegistry().Register(new StubHandler("system.doctor"));
        var gateway = CreateGateway(descriptor, registry, contextFactory: null);

        var result = await gateway.ExecuteAsync(Request("system.doctor"), Activity(), CancellationToken.None);

        Assert.Equal(OperationStatus.Unsupported, result.Status);
        Assert.Equal(ErrorCodes.CapabilityUnsupported, result.Errors[0].Code);
        Assert.Contains("基础设施", result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidActivityId_IsRejectedBeforeDispatch()
    {
        var descriptor = Descriptor("system.doctor", implemented: true);
        var registry = new OperationHandlerRegistry().Register(new StubHandler("system.doctor"));
        var gateway = CreateGateway(descriptor, registry, new StubContextFactory());

        var result = await gateway.ExecuteAsync(
            Request("system.doctor"),
            new ActivityContext { ActivityId = "../bad" },
            CancellationToken.None);

        Assert.Equal(ErrorCodes.InvalidArgument, result.Errors[0].Code);
        Assert.Equal(CliExitCode.InvalidArgument, ExitCodePolicy.Map(result));
    }

    [Fact]
    public async Task RegisteredHandler_ReturnsTypedDataAndRecordsActivity()
    {
        var descriptor = Descriptor("system.doctor", implemented: true);
        var registry = new OperationHandlerRegistry().Register(new StubHandler("system.doctor"));
        var sink = new StubActivitySink();
        var gateway = CreateGateway(descriptor, registry, new StubContextFactory(), sink);

        var result = await gateway.ExecuteAsync(Request("system.doctor"), Activity(), CancellationToken.None);

        var typed = Assert.IsType<OperationResult<DoctorData>>(result);
        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.Equal("ready", typed.Data.State);
        Assert.Equal(CliExitCode.Success, ExitCodePolicy.Map(typed));

        var record = Assert.Single(sink.Records);
        Assert.Equal("system.doctor", record.OperationId);
        Assert.Equal(OperationStatus.Completed, record.Status);
        Assert.Equal(0, record.ExitCode);
        Assert.NotNull(record.FinishedAt);
    }

    [Fact]
    public async Task HandlerException_BecomesExecutionFailure()
    {
        var descriptor = Descriptor("system.doctor", implemented: true);
        var registry = new OperationHandlerRegistry().Register(new ThrowingHandler("system.doctor"));
        var sink = new StubActivitySink();
        var gateway = CreateGateway(descriptor, registry, new StubContextFactory(), sink);

        var result = await gateway.ExecuteAsync(Request("system.doctor"), Activity(), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.ExecutionFailed, result.Errors[0].Code);
        Assert.Equal(CliExitCode.ExecutionFailed, ExitCodePolicy.Map(result));

        // 失败也必须留下独立审计记录，且退出码与错误码来自同一次结果映射。
        var record = Assert.Single(sink.Records);
        Assert.Equal(OperationStatus.Failed, record.Status);
        Assert.Equal(5, record.ExitCode);
        Assert.Equal(ErrorCodes.ExecutionFailed, record.Error!.Code);
        Assert.NotNull(record.FinishedAt);
    }

    [Fact]
    public async Task Snapshot_ReportsActivityPolicyAndNoProviders()
    {
        var gateway = CreateGateway(Descriptor("system.doctor"));

        var snapshot = await gateway.GetSnapshotAsync(CancellationToken.None);

        Assert.Empty(snapshot.Providers);
        Assert.Null(snapshot.Editor);
        Assert.Equal(64 * 1024, snapshot.ActivityArtifactThresholdBytes);
        Assert.Equal(30, snapshot.ActivityRetentionDays);
        Assert.Equal(BDToolContract.Version, snapshot.ContractVersion);
    }

    [Theory]
    [InlineData(OperationStatus.Completed, CliExitCode.Success)]
    [InlineData(OperationStatus.Unsupported, CliExitCode.CapabilityUnsupported)]
    [InlineData(OperationStatus.Cancelled, CliExitCode.Cancelled)]
    [InlineData(OperationStatus.Uncertain, CliExitCode.ExecutionUncertain)]
    [InlineData(OperationStatus.Partial, CliExitCode.ExecutionFailed)]
    public void ExitCodePolicy_MapsStatus(OperationStatus status, CliExitCode expected)
    {
        var result = new OperationResult { Status = status };
        Assert.Equal(expected, ExitCodePolicy.Map(result));
    }

    [Theory]
    [InlineData(ErrorCodes.InvalidArgument, CliExitCode.InvalidArgument)]
    [InlineData(ErrorCodes.ProjectNotFound, CliExitCode.EnvironmentNotReady)]
    [InlineData(ErrorCodes.Timeout, CliExitCode.Timeout)]
    [InlineData(ErrorCodes.Cancelled, CliExitCode.Cancelled)]
    [InlineData(ErrorCodes.ExecutionUncertain, CliExitCode.ExecutionUncertain)]
    [InlineData(ErrorCodes.ExecutionFailed, CliExitCode.ExecutionFailed)]
    public void ExitCodePolicy_MapsFailedErrorCode(string code, CliExitCode expected)
    {
        var result = new OperationResult { Status = OperationStatus.Failed };
        result.Errors.Add(new KernelError { Code = code });

        Assert.Equal(expected, ExitCodePolicy.Map(result));
    }

    [Fact]
    public async Task UnknownOperation_WritesFailedActivityWithSameActivityAndTraceIds()
    {
        var sink = new StubActivitySink();
        var gateway = CreateGateway(Descriptor("system.doctor"), sink: sink);

        await gateway.ExecuteAsync(Request("missing.operation"), Activity(), CancellationToken.None);

        var record = Assert.Single(sink.Records);
        Assert.Equal("act-0001", record.ActivityId);
        Assert.Equal("trace-0001", record.TraceId);
        Assert.Equal(ActivitySources.Test, record.Source);
        Assert.Equal("missing.operation", record.OperationId);
        Assert.Equal(OperationStatus.Failed, record.Status);
        Assert.Equal(2, record.ExitCode);
        Assert.Equal(ErrorCodes.NotFound, record.Error!.Code);
        Assert.NotEqual(default, record.StartedAt);
        Assert.NotNull(record.FinishedAt);
        Assert.True(record.FinishedAt >= record.StartedAt);
    }

    [Fact]
    public async Task UnsupportedOperation_WritesUnsupportedActivity()
    {
        var sink = new StubActivitySink();
        var gateway = CreateGateway(Descriptor("system.doctor"), sink: sink);

        await gateway.ExecuteAsync(Request("system.doctor"), Activity(), CancellationToken.None);

        var record = Assert.Single(sink.Records);
        Assert.Equal("act-0001", record.ActivityId);
        Assert.Equal("trace-0001", record.TraceId);
        Assert.Equal(ActivitySources.Test, record.Source);
        Assert.Equal(OperationStatus.Unsupported, record.Status);
        Assert.Equal(3, record.ExitCode);
        Assert.Equal(ErrorCodes.CapabilityUnsupported, record.Error!.Code);
        Assert.NotEqual(default, record.StartedAt);
        Assert.NotNull(record.FinishedAt);
    }

    [Fact]
    public void Gateway_RejectsMissingActivitySink()
    {
        var descriptor = Descriptor("system.doctor");
        var catalog = new StubCatalog(descriptor);
        var handlers = new OperationHandlerRegistry();
        var capabilities = new CatalogCapabilityInspector(catalog, handlers);

        // 审计端口是验收依赖；缺少时必须在构造阶段失败，而不是静默跳过 Activity。
        Assert.Throws<ArgumentNullException>(() => new ApplicationGateway(
            catalog,
            handlers,
            capabilities,
            contextFactory: null,
            activitySink: null!,
            projectPath: @"C:\project",
            appVersion: "0.1.0"));
    }

    [Fact]
    public async Task MissingRuntimeContext_WritesUnsupportedActivity()
    {
        var descriptor = Descriptor("system.doctor", implemented: true);
        var registry = new OperationHandlerRegistry().Register(new StubHandler("system.doctor"));
        var sink = new StubActivitySink();
        var gateway = CreateGateway(descriptor, registry, contextFactory: null, sink: sink);

        await gateway.ExecuteAsync(Request("system.doctor"), Activity(), CancellationToken.None);

        var record = Assert.Single(sink.Records);
        Assert.Equal(OperationStatus.Unsupported, record.Status);
        Assert.Equal(3, record.ExitCode);
    }

    [Fact]
    public async Task CancelledHandler_WritesCancelledActivity()
    {
        var descriptor = Descriptor("system.doctor", implemented: true);
        var registry = new OperationHandlerRegistry().Register(new CancelledHandler("system.doctor"));
        var sink = new StubActivitySink();
        var gateway = CreateGateway(descriptor, registry, new StubContextFactory(), sink);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await gateway.ExecuteAsync(
            Request("system.doctor"),
            Activity(),
            cancellation.Token);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(CliExitCode.Cancelled, ExitCodePolicy.Map(result));
        var record = Assert.Single(sink.Records);
        Assert.Equal(OperationStatus.Cancelled, record.Status);
        Assert.Equal(7, record.ExitCode);
        Assert.Equal(ErrorCodes.Cancelled, record.Error!.Code);
        Assert.NotNull(record.FinishedAt);
    }

    [Fact]
    public async Task ActivitySinkFailure_OnlyAddsWarning()
    {
        var descriptor = Descriptor("system.doctor", implemented: true);
        var registry = new OperationHandlerRegistry().Register(new StubHandler("system.doctor"));
        var gateway = CreateGateway(descriptor, registry, new StubContextFactory(), new ThrowingActivitySink());

        var result = await gateway.ExecuteAsync(Request("system.doctor"), Activity(), CancellationToken.None);

        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.Equal(CliExitCode.Success, ExitCodePolicy.Map(result));
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("ACTIVITY_WRITE_FAILED", warning.Code);
    }

    [Fact]
    public async Task ActivityPayload_UsesSharedRedactionPolicy()
    {
        var descriptor = Descriptor("system.doctor", implemented: true);
        var registry = new OperationHandlerRegistry().Register(new StubHandler("system.doctor"));
        var sink = new StubActivitySink();
        var gateway = CreateGateway(descriptor, registry, new StubContextFactory(), sink);

        await gateway.ExecuteAsync(
            Request("system.doctor", """{"token":"abcd1234","keep":"v"}"""),
            Activity(),
            CancellationToken.None);

        var record = Assert.Single(sink.Records);
        Assert.True(record.Input.Redacted);
        Assert.DoesNotContain("abcd1234", record.Input.PreviewJson, StringComparison.Ordinal);
        Assert.Contains("$redacted", record.Input.PreviewJson, StringComparison.Ordinal);
        Assert.Contains("\"keep\":\"v\"", record.Input.PreviewJson, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(record.Output.PreviewJson));
        Assert.Equal("Completed", record.Status.ToString());
    }

    private static ApplicationGateway CreateGateway(
        OperationDescriptor descriptor,
        IOperationHandlerRegistry? handlers = null,
        IOperationContextFactory? contextFactory = null,
        IActivitySink? sink = null)
    {
        var catalog = new StubCatalog(descriptor);
        handlers ??= new OperationHandlerRegistry();
        sink ??= new StubActivitySink();
        var capabilities = new CatalogCapabilityInspector(catalog, handlers);
        return new ApplicationGateway(
            catalog,
            handlers,
            capabilities,
            contextFactory,
            sink,
            projectPath: @"C:\project",
            appVersion: "0.1.0");
    }

    private static OperationDescriptor Descriptor(string id, bool implemented = false) =>
        new OperationDescriptor
        {
            Id = id,
            Description = "测试操作",
            Effect = OperationEffects.Read,
            Environment = new List<string> { OperationEnvironments.Workspace },
            Milestone = "M0",
            Implemented = implemented,
            Availability = implemented ? OperationAvailability.Available : OperationAvailability.ContractOnly
        };

    private static OperationRequest Request(string operationId, string inputJson = "{}") =>
        new OperationRequest { OperationId = operationId, ProjectPath = @"C:\project", InputJson = inputJson };

    private static ActivityContext Activity() =>
        new ActivityContext
        {
            ActivityId = "act-0001",
            TraceId = "trace-0001",
            Source = ActivitySources.Test,
            Actor = new ActivityActor { Type = ActorTypes.System }
        };

    private sealed class StubCatalog : IOperationCatalog
    {
        private readonly List<OperationDescriptor> _items;

        public StubCatalog(OperationDescriptor descriptor) => _items = new List<OperationDescriptor> { descriptor };

        public IReadOnlyList<OperationDescriptor> GetAll() => _items;

        public OperationDescriptor? Find(string? operationId) =>
            _items.Find(item => string.Equals(item.Id, operationId, StringComparison.Ordinal));
    }

    private sealed class StubHandler : IOperationHandler
    {
        public StubHandler(string operationId) => OperationId = operationId;

        public string OperationId { get; }

        public Task<OperationResult> ExecuteAsync(
            OperationRequest request,
            IOperationContext context,
            CancellationToken cancellationToken)
        {
            OperationResult result = new OperationResult<DoctorData>
            {
                Status = OperationStatus.Completed,
                Data = new DoctorData { State = "ready" }
            };
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingHandler : IOperationHandler
    {
        public ThrowingHandler(string operationId) => OperationId = operationId;

        public string OperationId { get; }

        public Task<OperationResult> ExecuteAsync(
            OperationRequest request,
            IOperationContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class CancelledHandler : IOperationHandler
    {
        public CancelledHandler(string operationId) => OperationId = operationId;

        public string OperationId { get; }

        public Task<OperationResult> ExecuteAsync(
            OperationRequest request,
            IOperationContext context,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException(cancellationToken);
    }

    private sealed class StubContextFactory : IOperationContextFactory
    {
        public IOperationContext? TryCreate(OperationRequest request) => new StubContext();
    }

    private sealed class StubContext : IOperationContext
    {
        public IWorkspaceKernel Workspace => null!;
        public IEditorKernel Editor => null!;
        public IArtifactStore Artifacts => null!;
        public ICapabilityInspector Capabilities => null!;
    }

    private sealed class StubActivitySink : IActivitySink
    {
        public List<ActivityRecord> Records { get; } = new List<ActivityRecord>();

        public Task RecordAsync(ActivityRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingActivitySink : IActivitySink
    {
        public Task RecordAsync(ActivityRecord record, CancellationToken cancellationToken) =>
            throw new IOException("disk full");
    }

    private sealed class DoctorData
    {
        public string State { get; set; } = string.Empty;
    }
}
