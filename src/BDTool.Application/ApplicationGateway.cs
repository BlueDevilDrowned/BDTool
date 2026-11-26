using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Activity;
using BDTool.Contracts;
using BDTool.Contracts.Activity;
using BDTool.Contracts.Capabilities;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Gateway;
using BDTool.Contracts.Operations;
using BDTool.Core.Activity;
using BDTool.Core.Operations;
using BDTool.Operations.Handlers;

namespace BDTool.Application;

/// <summary>
/// 进程内 IApplicationGateway 实现：解析目录、检查可用性、交给 Handler，
/// 并把每一次有效调用（成功、未知、未支持、失败、取消）都尽力写成独立 Activity。
/// Activity 写入失败只追加告警，不改变业务结果；Activity 端口是构造必需依赖，
/// 缺少运行基础设施时返回结构化失败，不会伪造成功。
/// </summary>
public sealed class ApplicationGateway : IApplicationGateway
{
    private readonly IOperationCatalog _catalog;
    private readonly IOperationHandlerRegistry _handlers;
    private readonly ICapabilityInspector _capabilities;
    private readonly IOperationContextFactory? _contextFactory;
    private readonly IActivitySink _activitySink;
    private readonly string _projectPath;
    private readonly string _appVersion;

    /// <summary>显式构造函数；所有依赖由组合根传入，网关不自行创建基础设施。</summary>
    public ApplicationGateway(
        IOperationCatalog catalog,
        IOperationHandlerRegistry handlers,
        ICapabilityInspector capabilities,
        IOperationContextFactory? contextFactory,
        IActivitySink activitySink,
        string projectPath,
        string appVersion)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _contextFactory = contextFactory;
        // 审计是 M0 的验收项：拒绝无 sink 构造，避免调用成功却没有可查询记录。
        _activitySink = activitySink ?? throw new ArgumentNullException(nameof(activitySink));
        _projectPath = projectPath ?? string.Empty;
        _appVersion = string.IsNullOrWhiteSpace(appVersion) ? "0.0.0" : appVersion;
    }

    /// <inheritdoc />
    public async Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var capabilitySnapshot = await _capabilities.GetAsync(cancellationToken).ConfigureAwait(false);
        return new ApplicationSnapshot
        {
            AppVersion = _appVersion,
            ContractVersion = BDToolContract.Version,
            ProjectPath = _projectPath,
            ProjectExists = _projectPath.Length > 0 && Directory.Exists(_projectPath),
            Providers = new List<Contracts.Providers.ProviderDescriptor>(),
            Capabilities = capabilitySnapshot,
            Editor = null,
            GeneratedAt = DateTimeOffset.UtcNow,
            ActivityArtifactThresholdBytes = ActivityPolicy.LargeContentThresholdBytes,
            ActivityRetentionDays = ActivityPolicy.DefaultRetentionDays
        };
    }

    /// <inheritdoc />
    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        ActivityContext activity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(activity);

        // 参数级错误发生在调用边界之前，无法安全构造 Activity 文件名，因此直接返回。
        if (!OperationIdRules.IsValid(request.OperationId, out var idError))
        {
            return Failure(ErrorCodes.InvalidArgument, "input", idError);
        }

        if (!ActivityPaths.IsValidActivityId(activity.ActivityId, out var activityError))
        {
            return Failure(ErrorCodes.InvalidArgument, "activity", activityError);
        }

        // 记录在分发前建立，确保 unknown/unsupported 也留下审计轨迹；
        // ActivityId 与 TraceId 来自同一个调用上下文，全程保持一致。
        var record = ActivityRecordFactory.CreateStarted(activity, request, DateTimeOffset.UtcNow);

        var result = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);

        await TryRecordActivityAsync(record, result).ConfigureAwait(false);

        return result;
    }

    /// <summary>按目录、Handler、运行上下文顺序分发；所有分支都返回结构化结果。</summary>
    private async Task<OperationResult> DispatchAsync(
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        var descriptor = _catalog.Find(request.OperationId);
        if (descriptor is null)
        {
            return Failure(
                ErrorCodes.NotFound,
                "catalog",
                $"操作目录中不存在：{request.OperationId}",
                new Dictionary<string, string> { ["operationId"] = request.OperationId });
        }

        var handler = _handlers.Find(request.OperationId);
        if (handler is null
            || !descriptor.Implemented
            || !string.Equals(descriptor.Availability, OperationAvailability.Available, StringComparison.Ordinal))
        {
            return Unsupported(
                request.OperationId,
                "当前构建未注册该操作的 Handler。",
                descriptor.Milestone);
        }

        var context = _contextFactory?.TryCreate(request);
        if (context is null)
        {
            return Unsupported(
                request.OperationId,
                "运行环境未装配 Workspace/Editor/Artifact 基础设施。",
                descriptor.Milestone);
        }

        try
        {
            var result = await handler.ExecuteAsync(request, context, cancellationToken).ConfigureAwait(false);
            return result ?? Failure(ErrorCodes.InternalError, "execution", "Handler 返回了空结果。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(ErrorCodes.Cancelled, "execution", "调用已取消。", status: OperationStatus.Cancelled);
        }
        catch (Exception exception)
        {
            return Failure(
                ErrorCodes.ExecutionFailed,
                "execution",
                "Handler 执行时发生未处理异常。",
                new Dictionary<string, string> { ["exceptionType"] = exception.GetType().Name });
        }
    }

    /// <summary>
    /// 补全并写入 Activity。写入异常只产生 ACTIVITY_WRITE_FAILED 告警，
    /// 即使业务操作成功也不会因为审计存储故障被改成失败。
    /// </summary>
    private async Task TryRecordActivityAsync(ActivityRecord record, OperationResult result)
    {
        ActivityRecordFactory.Complete(record, result, (int)ExitCodePolicy.Map(result), DateTimeOffset.UtcNow);
        try
        {
            // 审计写入使用独立令牌，避免调用方取消导致记录只停留在 Running。
            await _activitySink.RecordAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result.Warnings.Add(new KernelWarning
            {
                Code = "ACTIVITY_WRITE_FAILED",
                Message = "Activity 写入失败，业务结果仍然有效。",
                Details = new Dictionary<string, string> { ["exceptionType"] = exception.GetType().Name }
            });
        }
    }

    private static OperationResult Unsupported(string operationId, string reason, string milestone)
    {
        return Failure(
            ErrorCodes.CapabilityUnsupported,
            "capability",
            reason,
            new Dictionary<string, string>
            {
                ["operationId"] = operationId,
                ["milestone"] = milestone
            },
            OperationStatus.Unsupported);
    }

    private static OperationResult Failure(
        string code,
        string stage,
        string message,
        Dictionary<string, string>? details = null,
        OperationStatus status = OperationStatus.Failed)
    {
        var result = new OperationResult { Status = status };
        result.Errors.Add(new KernelError
        {
            Code = code,
            Stage = stage,
            Message = message,
            Retryable = false,
            Details = details ?? new Dictionary<string, string>()
        });
        return result;
    }
}
