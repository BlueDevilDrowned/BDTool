using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Activity;
using BDTool.Application;
using BDTool.Contracts;
using BDTool.Contracts.Activity;
using BDTool.Contracts.Artifacts;
using BDTool.Contracts.Capabilities;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Gateway;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;
using BDTool.Contracts.Workspace;
using BDTool.Core.Workspace;
using BDTool.Operations.Handlers;
using BDTool.Tests.Support;

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

    [Fact]
    public async Task Snapshot_ReportsInjectedProviderAndItsCapabilities()
    {
        var provider = new ProviderDescriptor
        {
            Id = "local",
            DisplayName = "Local Workspace",
            Kind = ProviderKinds.Workspace,
            Capabilities = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [CapabilityIds.WorkspaceRead] = true,
                [CapabilityIds.WorkspaceWrite] = false
            }
        };
        var gateway = CreateGateway(Descriptor("system.doctor"), providers: new[] { provider });

        var snapshot = await gateway.GetSnapshotAsync(CancellationToken.None);

        var reported = Assert.Single(snapshot.Providers);
        Assert.Equal("local", reported.Id);
        Assert.True(snapshot.Capabilities.Capabilities[CapabilityIds.WorkspaceRead]);
        Assert.False(snapshot.Capabilities.Capabilities[CapabilityIds.WorkspaceWrite]);
    }

    [Fact]
    public async Task CapabilitySnapshot_OnlyReportsSoleProviderId()
    {
        var catalog = new StubCatalog(Descriptor("system.doctor"));
        var handlers = new OperationHandlerRegistry();

        var none = await new CatalogCapabilityInspector(catalog, handlers)
            .GetAsync(CancellationToken.None);
        Assert.Equal(string.Empty, none.ProviderId);

        var local = Provider();
        local.Id = "local";
        var one = await new CatalogCapabilityInspector(catalog, handlers, new[] { local })
            .GetAsync(CancellationToken.None);
        Assert.Equal("local", one.ProviderId);

        var editor = Provider();
        editor.Id = "akio";
        var multiple = await new CatalogCapabilityInspector(catalog, handlers, new[] { local, editor })
            .GetAsync(CancellationToken.None);
        Assert.Equal(string.Empty, multiple.ProviderId);
    }

    [Fact]
    public async Task MissingRequiredCapability_ReturnsCapabilityUnsupportedAndExitCodeThree()
    {
        var descriptor = Descriptor("workspace.read", implemented: true);
        descriptor.RequiredCapabilities.Add(CapabilityIds.WorkspaceRead);
        var registry = new OperationHandlerRegistry().Register(new StubHandler("workspace.read"));
        var sink = new StubActivitySink();
        var gateway = CreateGateway(
            descriptor,
            registry,
            new StubContextFactory(),
            sink,
            capabilities: new EmptyCapabilityInspector());

        var result = await gateway.ExecuteAsync(Request("workspace.read"), Activity(), CancellationToken.None);

        Assert.Equal(OperationStatus.Unsupported, result.Status);
        Assert.Equal(ErrorCodes.CapabilityUnsupported, result.Errors[0].Code);
        Assert.Contains(CapabilityIds.WorkspaceRead, result.Errors[0].Details["missingCapabilities"], StringComparison.Ordinal);
        Assert.Equal(CliExitCode.CapabilityUnsupported, ExitCodePolicy.Map(result));
        var record = Assert.Single(sink.Records);
        Assert.Equal(3, record.ExitCode);
    }

    [Fact]
    public async Task ProviderReportsRequiredCapabilityFalse_HandlerRegistered_StillUnsupported()
    {
        // 回归：Handler 已注册不能把 Provider 明确上报的 false 抬成 true。
        // 操作 ID 与能力 ID 同名（workspace.read），正是旧逻辑被覆盖的那条路径。
        var descriptor = Descriptor("workspace.read", implemented: true);
        descriptor.RequiredCapabilities.Add(CapabilityIds.WorkspaceRead);
        var registry = new OperationHandlerRegistry().Register(new StubHandler("workspace.read"));
        var sink = new StubActivitySink();
        var providers = new[]
        {
            Provider(
                (CapabilityIds.WorkspaceRead, false),
                (CapabilityIds.WorkspaceSearch, true))
        };
        var gateway = CreateGateway(descriptor, registry, new StubContextFactory(), sink, providers);

        var result = await gateway.ExecuteAsync(Request("workspace.read"), Activity(), CancellationToken.None);

        Assert.Equal(OperationStatus.Unsupported, result.Status);
        Assert.Equal(ErrorCodes.CapabilityUnsupported, result.Errors[0].Code);
        Assert.Equal(
            CapabilityIds.WorkspaceRead,
            result.Errors[0].Details["missingCapabilities"]);
        Assert.Equal(CliExitCode.CapabilityUnsupported, ExitCodePolicy.Map(result));
        var record = Assert.Single(sink.Records);
        Assert.Equal(3, record.ExitCode);
        Assert.Equal(ErrorCodes.CapabilityUnsupported, record.Error!.Code);
    }

    [Fact]
    public async Task ProviderReportsRequiredCapabilityTrue_HandlerRegistered_ExecutesWithFlatSnapshot()
    {
        // 正向路径：Provider 上报 true 时门禁放行，且扁平快照形状保持不变。
        var descriptor = Descriptor("workspace.read", implemented: true);
        descriptor.RequiredCapabilities.Add(CapabilityIds.WorkspaceRead);
        var registry = new OperationHandlerRegistry().Register(new StubHandler("workspace.read"));
        var sink = new StubActivitySink();
        var providers = new[] { Provider((CapabilityIds.WorkspaceRead, true)) };
        var gateway = CreateGateway(descriptor, registry, new StubContextFactory(), sink, providers);

        var result = await gateway.ExecuteAsync(Request("workspace.read"), Activity(), CancellationToken.None);

        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.Equal(CliExitCode.Success, ExitCodePolicy.Map(result));
        Assert.Empty(result.Errors);

        var snapshot = await gateway.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal("local", snapshot.Capabilities.ProviderId);
        Assert.True(snapshot.Capabilities.Capabilities[CapabilityIds.WorkspaceRead]);
    }

    [Fact]
    public async Task ProviderOmitsRequiredCapability_HandlerRegistered_StillUnsupported()
    {
        // 未上报不等于支持：Provider 只报了别的能力时，门禁必须拒绝该能力。
        var descriptor = Descriptor("workspace.fingerprint", implemented: true);
        descriptor.RequiredCapabilities.Add(CapabilityIds.WorkspaceFingerprint);
        var registry = new OperationHandlerRegistry().Register(new StubHandler("workspace.fingerprint"));
        var sink = new StubActivitySink();
        var providers = new[] { Provider((CapabilityIds.WorkspaceRead, true)) };
        var gateway = CreateGateway(descriptor, registry, new StubContextFactory(), sink, providers);

        var result = await gateway.ExecuteAsync(Request("workspace.fingerprint"), Activity(), CancellationToken.None);

        Assert.Equal(OperationStatus.Unsupported, result.Status);
        Assert.Equal(ErrorCodes.CapabilityUnsupported, result.Errors[0].Code);
        Assert.Equal(
            CapabilityIds.WorkspaceFingerprint,
            result.Errors[0].Details["missingCapabilities"]);
        Assert.Equal(CliExitCode.CapabilityUnsupported, ExitCodePolicy.Map(result));
        Assert.Equal(3, Assert.Single(sink.Records).ExitCode);
    }

    [Fact]
    public async Task HandlerTimeout_ReturnsTimeoutFailureAndTerminalActivity()
    {
        var descriptor = Descriptor("system.doctor", implemented: true);
        descriptor.TimeoutSeconds = 1;
        var registry = new OperationHandlerRegistry().Register(new SlowHandler("system.doctor"));
        var sink = new StubActivitySink();
        var gateway = CreateGateway(
            descriptor,
            registry,
            new StubContextFactory(),
            sink,
            capabilities: new EmptyCapabilityInspector());

        var result = await gateway.ExecuteAsync(Request("system.doctor"), Activity(), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.Timeout, result.Errors[0].Code);
        Assert.Equal(CliExitCode.Timeout, ExitCodePolicy.Map(result));
        var record = Assert.Single(sink.Records);
        Assert.Equal(OperationStatus.Failed, record.Status);
        Assert.Equal(6, record.ExitCode);
        Assert.Equal(ErrorCodes.Timeout, record.Error!.Code);
        Assert.NotNull(record.FinishedAt);
    }

    [Fact]
    public async Task HandlerWorkspaceException_PreservesCodeAndDetails()
    {
        var descriptor = Descriptor("system.doctor", implemented: true);
        var registry = new OperationHandlerRegistry().Register(new WorkspaceThrowingHandler("system.doctor"));
        var sink = new StubActivitySink();
        var gateway = CreateGateway(
            descriptor,
            registry,
            new StubContextFactory(),
            sink,
            capabilities: new EmptyCapabilityInspector());

        var result = await gateway.ExecuteAsync(Request("system.doctor"), Activity(), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.PreconditionFailed, result.Errors[0].Code);
        Assert.Equal("abc123", result.Errors[0].Details["expectedSha256"]);
        Assert.Equal(CliExitCode.ExecutionFailed, ExitCodePolicy.Map(result));
        var record = Assert.Single(sink.Records);
        Assert.Equal(ErrorCodes.PreconditionFailed, record.Error!.Code);
        Assert.Equal(5, record.ExitCode);
    }

    [Fact]
    public async Task HandlerEditorException_PreservesCodeDetailsAndEditorStage()
    {
        var descriptor = Descriptor("system.doctor", implemented: true);
        var exception = new EditorException(
            ErrorCodes.PreconditionFailed,
            "Editor 项目不匹配。",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = "project-mismatch"
            });
        var registry = new OperationHandlerRegistry().Register(
            new EditorThrowingHandler("system.doctor", exception));
        var sink = new StubActivitySink();
        var gateway = CreateGateway(
            descriptor,
            registry,
            new StubContextFactory(),
            sink,
            capabilities: new EmptyCapabilityInspector());

        var result = await gateway.ExecuteAsync(Request("system.doctor"), Activity(), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.PreconditionFailed, result.Errors[0].Code);
        Assert.Equal("editor", result.Errors[0].Stage);
        Assert.Equal("project-mismatch", result.Errors[0].Details["reason"]);
        Assert.Equal(CliExitCode.ExecutionFailed, ExitCodePolicy.Map(result));

        var record = Assert.Single(sink.Records);
        Assert.Equal(ErrorCodes.PreconditionFailed, record.Error!.Code);
        Assert.Equal("editor", record.Error.Stage);
        Assert.Equal(5, record.ExitCode);
    }

    [Fact]
    public async Task HandlerEditorCancelled_MapsToCancelledStatusAndExitCode()
    {
        var descriptor = Descriptor("system.doctor", implemented: true);
        var registry = new OperationHandlerRegistry().Register(
            new EditorThrowingHandler(
                "system.doctor",
                new EditorException(ErrorCodes.Cancelled, "Editor 操作已取消。")));
        var sink = new StubActivitySink();
        var gateway = CreateGateway(
            descriptor,
            registry,
            new StubContextFactory(),
            sink,
            capabilities: new EmptyCapabilityInspector());

        var result = await gateway.ExecuteAsync(Request("system.doctor"), Activity(), CancellationToken.None);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(ErrorCodes.Cancelled, result.Errors[0].Code);
        Assert.Equal(CliExitCode.Cancelled, ExitCodePolicy.Map(result));
        var record = Assert.Single(sink.Records);
        Assert.Equal(OperationStatus.Cancelled, record.Status);
        Assert.Equal(7, record.ExitCode);
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

    [Fact]
    public async Task LargeInputAndOutput_WriteVerifiableArtifactsWithoutSecrets()
    {
        using var project = new TemporaryProjectDirectory();
        var descriptor = Descriptor("workspace.read", implemented: true);
        var registry = new OperationHandlerRegistry().Register(new LargeOutputHandler("workspace.read"));
        var sink = new StubActivitySink();
        var artifacts = new FileArtifactStore(project.ProjectPath);
        var gateway = CreateGateway(
            descriptor,
            registry,
            new StubContextFactory(),
            sink,
            artifacts: artifacts,
            projectPath: project.ProjectPath);

        var secret = "super-secret-value-123";
        var largeInput = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["token"] = secret,
            ["blob"] = new string('a', 70 * 1024)
        });

        var result = await gateway.ExecuteAsync(
            Request("workspace.read", largeInput),
            Activity(),
            CancellationToken.None);

        Assert.Equal(OperationStatus.Completed, result.Status);
        var record = Assert.Single(sink.Records);
        AssertArtifactMatches(project, record.Input.Artifact);
        AssertArtifactMatches(project, record.Output.Artifact);
        Assert.Contains("\"$artifact\"", record.Input.PreviewJson, StringComparison.Ordinal);
        Assert.Contains("\"$artifact\"", record.Output.PreviewJson, StringComparison.Ordinal);

        // Artifact 内容来自脱敏后的载荷，secret 不能原样落盘。
        var inputArtifact = record.Input.Artifact!;
        var inputBytes = File.ReadAllBytes(ResolveArtifact(project, inputArtifact));
        Assert.Equal(record.Input.Artifact!.Sha256, Sha256(inputBytes));
        var inputText = Encoding.UTF8.GetString(inputBytes);
        Assert.DoesNotContain(secret, inputText, StringComparison.Ordinal);
        Assert.Contains("$redacted", inputText, StringComparison.Ordinal);
        Assert.Contains(inputArtifact.Sha256, record.Input.PreviewJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArtifactWriteFailure_OnlyAddsWarningAndKeepsBusinessResult()
    {
        var descriptor = Descriptor("workspace.read", implemented: true);
        var registry = new OperationHandlerRegistry().Register(new LargeOutputHandler("workspace.read"));
        var sink = new StubActivitySink();
        var gateway = CreateGateway(
            descriptor,
            registry,
            new StubContextFactory(),
            sink,
            artifacts: new ThrowingArtifactStore());

        var result = await gateway.ExecuteAsync(
            Request("workspace.read"),
            Activity(),
            CancellationToken.None);

        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.Equal(CliExitCode.Success, ExitCodePolicy.Map(result));
        Assert.Contains(
            result.Warnings,
            warning => warning.Code == ApplicationGateway.ActivityArtifactWriteFailed);
        var record = Assert.Single(sink.Records);
        Assert.Null(record.Output.Artifact);
    }

    [Fact]
    public async Task ArtifactVerifyMismatch_ExcludesReferencesButKeepsBusinessResultAndFile()
    {
        using var project = new TemporaryProjectDirectory();
        var descriptor = Descriptor("workspace.read", implemented: true);
        var registry = new OperationHandlerRegistry().Register(new LargeOutputHandler("workspace.read"));
        var sink = new StubActivitySink();
        var artifacts = new MismatchedArtifactStore(project.ProjectPath);
        var gateway = CreateGateway(
            descriptor,
            registry,
            new StubContextFactory(),
            sink,
            artifacts: artifacts,
            projectPath: project.ProjectPath);

        // 输入与输出都超过阈值，确保两条 Artifact 路径都会经过校验分支。
        var largeInput = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["blob"] = new string('a', 70 * 1024)
        });

        var result = await gateway.ExecuteAsync(
            Request("workspace.read", largeInput),
            Activity(),
            CancellationToken.None);

        // 校验失败属于审计诊断，业务结果与退出码必须保持成功。
        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.Equal(CliExitCode.Success, ExitCodePolicy.Map(result));
        Assert.Equal(
            2,
            result.Warnings.Count(warning => warning.Code == ApplicationGateway.ActivityArtifactVerifyFailed));

        var record = Assert.Single(sink.Records);
        // 不可信引用不得挂到 Activity 载荷上，否则调用方读取时会拿到与摘要不符的内容。
        Assert.Null(record.Input.Artifact);
        Assert.Null(record.Output.Artifact);
        // 摘要预览仍保留 $artifact 标记与哈希，便于人工定位问题。
        Assert.Contains("\"$artifact\"", record.Input.PreviewJson, StringComparison.Ordinal);
        Assert.Contains("\"$artifact\"", record.Output.PreviewJson, StringComparison.Ordinal);

        // 文件本身没有被删除：本次只是排除引用，存储尝试次数证明写入确实发生过。
        Assert.Equal(2, artifacts.WriteCount);
        var artifactRoot = Path.Combine(project.ProjectPath, ".bdtool", "artifacts");
        Assert.Equal(2, Directory.GetFiles(artifactRoot, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task MissingProject_SkipsAuditWithoutCreatingDirectory()
    {
        var missingProject = Path.Combine(
            Path.GetTempPath(),
            "bdtool-tests",
            Guid.NewGuid().ToString("n"),
            "missing");
        Assert.False(Directory.Exists(missingProject));
        var descriptor = Descriptor("system.doctor", implemented: true);
        var registry = new OperationHandlerRegistry().Register(new StubHandler("system.doctor"));
        var sink = new StubActivitySink();
        var gateway = CreateGateway(
            descriptor,
            registry,
            new StubContextFactory(),
            sink,
            projectPath: missingProject);

        var result = await gateway.ExecuteAsync(
            Request("system.doctor"),
            Activity(),
            CancellationToken.None);

        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.Contains(
            result.Warnings,
            warning => warning.Code == ApplicationGateway.AuditUnavailableProjectMissing);
        Assert.Empty(sink.Records);
        Assert.False(Directory.Exists(missingProject), "审计不得为缺失项目创建目录。");
    }

    [Fact]
    public async Task SuccessfulExecution_RecordsInjectedProviderId()
    {
        var descriptor = Descriptor("system.doctor", implemented: true);
        var registry = new OperationHandlerRegistry().Register(new StubHandler("system.doctor"));
        var sink = new StubActivitySink();
        var gateway = CreateGateway(
            descriptor,
            registry,
            new StubContextFactory(),
            sink,
            executionProviderId: "local");

        await gateway.ExecuteAsync(Request("system.doctor"), Activity(), CancellationToken.None);

        Assert.Equal("local", Assert.Single(sink.Records).ProviderId);
    }

    [Fact]
    public async Task UnknownOperation_AndMissingProvider_RecordEmptyProviderId()
    {
        var descriptor = Descriptor("system.doctor", implemented: true);
        var registry = new OperationHandlerRegistry().Register(new StubHandler("system.doctor"));
        var sink = new StubActivitySink();
        var gateway = CreateGateway(
            descriptor,
            registry,
            new StubContextFactory(),
            sink,
            executionProviderId: "local");

        await gateway.ExecuteAsync(Request("missing.operation"), Activity(), CancellationToken.None);

        var unknownRecord = Assert.Single(sink.Records);
        Assert.Equal(string.Empty, unknownRecord.ProviderId);

        var noProviderGateway = CreateGateway(
            descriptor,
            registry,
            new StubContextFactory(),
            sink);
        await noProviderGateway.ExecuteAsync(Request("system.doctor"), Activity(), CancellationToken.None);

        Assert.Equal(string.Empty, sink.Records[1].ProviderId);
    }

    private static ApplicationGateway CreateGateway(
        OperationDescriptor descriptor,
        IOperationHandlerRegistry? handlers = null,
        IOperationContextFactory? contextFactory = null,
        IActivitySink? sink = null,
        IReadOnlyList<ProviderDescriptor>? providers = null,
        ICapabilityInspector? capabilities = null,
        string? projectPath = null,
        string? executionProviderId = null,
        IArtifactStore? artifacts = null)
    {
        var catalog = new StubCatalog(descriptor);
        handlers ??= new OperationHandlerRegistry();
        sink ??= new StubActivitySink();
        capabilities ??= new CatalogCapabilityInspector(catalog, handlers, providers);
        // 使用真实存在的目录，保证审计写入路径不被“项目缺失”保护短路。
        return new ApplicationGateway(
            catalog,
            handlers,
            capabilities,
            contextFactory,
            sink,
            projectPath: projectPath ?? AppContext.BaseDirectory,
            appVersion: "0.1.0",
            providers: providers,
            executionProviderId: executionProviderId,
            artifacts: artifacts);
    }

    private static void AssertArtifactMatches(TemporaryProjectDirectory project, ArtifactReference? reference)
    {
        Assert.NotNull(reference);
        var path = ResolveArtifact(project, reference!);
        Assert.True(File.Exists(path), "Artifact 文件必须真实落盘：" + path);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(reference!.SizeBytes, bytes.LongLength);
        Assert.Equal(reference.Sha256, Sha256(bytes));
    }

    private static string ResolveArtifact(TemporaryProjectDirectory project, ArtifactReference reference) =>
        Path.Combine(project.ProjectPath, reference.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

    /// <summary>构造一个只上报指定能力的 Workspace Provider 描述。</summary>
    private static ProviderDescriptor Provider(params (string Key, bool Value)[] capabilities)
    {
        var descriptor = new ProviderDescriptor
        {
            Id = "local",
            DisplayName = "Local Workspace",
            Kind = ProviderKinds.Workspace
        };

        foreach (var capability in capabilities)
        {
            descriptor.Capabilities[capability.Key] = capability.Value;
        }

        return descriptor;
    }

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

    /// <summary>返回超过大内容阈值的结果，用于验证输出 Artifact 路径。</summary>
    private sealed class LargeOutputHandler : IOperationHandler
    {
        public LargeOutputHandler(string operationId) => OperationId = operationId;

        public string OperationId { get; }

        public Task<OperationResult> ExecuteAsync(
            OperationRequest request,
            IOperationContext context,
            CancellationToken cancellationToken)
        {
            OperationResult result = new OperationResult<LargeData>
            {
                Status = OperationStatus.Completed,
                Data = new LargeData { Payload = new string('b', 80 * 1024) }
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

    /// <summary>模拟慢操作，用于验证目录超时被真实执行而不是只存在于元数据。</summary>
    private sealed class SlowHandler : IOperationHandler
    {
        public SlowHandler(string operationId) => OperationId = operationId;

        public string OperationId { get; }

        public async Task<OperationResult> ExecuteAsync(
            OperationRequest request,
            IOperationContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new OperationResult<DoctorData>
            {
                Status = OperationStatus.Completed,
                Data = new DoctorData { State = "ready" }
            };
        }
    }

    /// <summary>模拟 Handler 漏捕 WorkspaceException 时网关仍保留原始错误码。</summary>
    private sealed class WorkspaceThrowingHandler : IOperationHandler
    {
        public WorkspaceThrowingHandler(string operationId) => OperationId = operationId;

        public string OperationId { get; }

        public Task<OperationResult> ExecuteAsync(
            OperationRequest request,
            IOperationContext context,
            CancellationToken cancellationToken) =>
            throw new WorkspaceException(
                ErrorCodes.PreconditionFailed,
                "CAS 不匹配。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["expectedSha256"] = "abc123"
                });
    }

    /// <summary>模拟 Provider 已转换的 Editor 失败；网关必须原样保留错误码与详情。</summary>
    private sealed class EditorThrowingHandler : IOperationHandler
    {
        private readonly EditorException _exception;

        public EditorThrowingHandler(string operationId, EditorException exception)
        {
            OperationId = operationId;
            _exception = exception;
        }

        public string OperationId { get; }

        public Task<OperationResult> ExecuteAsync(
            OperationRequest request,
            IOperationContext context,
            CancellationToken cancellationToken) => throw _exception;
    }

    private sealed class EmptyCapabilityInspector : ICapabilityInspector
    {
        public Task<CapabilitySnapshot> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CapabilitySnapshot());
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

    /// <summary>模拟 Artifact 存储不可写；网关必须只追加 Warning 而不改变业务结果。</summary>
    private sealed class ThrowingArtifactStore : IArtifactStore
    {
        public Task<ArtifactReference> WriteAsync(ArtifactWriteRequest request, CancellationToken cancellationToken) =>
            throw new IOException("no space");

        public Task<ArtifactContent> ReadAsync(ArtifactReference reference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(ArtifactReference reference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// 用真实文件存储落盘，但返回被动过手脚的元数据，模拟“文件写了、引用不可信”的校验失败场景。
    /// 用于验证网关既不挂载不可信引用，也不删除已写文件。
    /// </summary>
    private sealed class MismatchedArtifactStore : IArtifactStore
    {
        private readonly FileArtifactStore _inner;

        public MismatchedArtifactStore(string projectPath) => _inner = new FileArtifactStore(projectPath);

        /// <summary>记录真实写入次数，证明引用被排除不是因为写入被跳过。</summary>
        public int WriteCount { get; private set; }

        public async Task<ArtifactReference> WriteAsync(
            ArtifactWriteRequest request,
            CancellationToken cancellationToken)
        {
            var reference = await _inner.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            WriteCount++;
            reference.SizeBytes += 1;
            reference.Sha256 = new string('0', 64);
            return reference;
        }

        public Task<ArtifactContent> ReadAsync(ArtifactReference reference, CancellationToken cancellationToken) =>
            _inner.ReadAsync(reference, cancellationToken);

        public Task<bool> ExistsAsync(ArtifactReference reference, CancellationToken cancellationToken) =>
            _inner.ExistsAsync(reference, cancellationToken);
    }

    private sealed class DoctorData
    {
        public string State { get; set; } = string.Empty;
    }

    private sealed class LargeData
    {
        public string Payload { get; set; } = string.Empty;
    }
}
