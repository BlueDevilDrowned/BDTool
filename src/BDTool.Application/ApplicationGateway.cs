using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Activity;
using BDTool.Contracts;
using BDTool.Contracts.Activity;
using BDTool.Contracts.Artifacts;
using BDTool.Contracts.Capabilities;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Gateway;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;
using BDTool.Core.Activity;
using BDTool.Core.Operations;
using BDTool.Core.Workspace;
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
    /// <summary>绑定项目根不存在时跳过审计写入的稳定告警码。</summary>
    public const string AuditUnavailableProjectMissing = "AUDIT_UNAVAILABLE_PROJECT_MISSING";

    /// <summary>Artifact 写入失败时附加的稳定告警码；业务结果不变。</summary>
    public const string ActivityArtifactWriteFailed = "ACTIVITY_ARTIFACT_WRITE_FAILED";

    /// <summary>需要 Artifact 但网关未装配 Artifact Store 时的稳定告警码。</summary>
    public const string ActivityArtifactUnavailable = "ACTIVITY_ARTIFACT_UNAVAILABLE";

    /// <summary>Artifact 落盘后大小或哈希与摘要不一致时的稳定告警码。</summary>
    public const string ActivityArtifactVerifyFailed = "ACTIVITY_ARTIFACT_VERIFY_FAILED";

    private readonly IOperationCatalog _catalog;
    private readonly IOperationHandlerRegistry _handlers;
    private readonly ICapabilityInspector _capabilities;
    private readonly IOperationContextFactory? _contextFactory;
    private readonly IActivitySink _activitySink;
    private readonly IArtifactStore? _artifacts;
    private readonly IReadOnlyList<ProviderDescriptor> _providers;
    private readonly string _projectPath;
    private readonly string _appVersion;
    private readonly string _executionProviderId;

    /// <summary>显式构造函数；所有依赖由组合根传入，网关不自行创建基础设施。</summary>
    public ApplicationGateway(
        IOperationCatalog catalog,
        IOperationHandlerRegistry handlers,
        ICapabilityInspector capabilities,
        IOperationContextFactory? contextFactory,
        IActivitySink activitySink,
        string projectPath,
        string appVersion,
        IReadOnlyList<ProviderDescriptor>? providers = null,
        IArtifactStore? artifacts = null,
        string? executionProviderId = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _contextFactory = contextFactory;
        // 审计是 M0 的验收项：拒绝无 sink 构造，避免调用成功却没有可查询记录。
        _activitySink = activitySink ?? throw new ArgumentNullException(nameof(activitySink));
        _artifacts = artifacts;
        _providers = providers is null
            ? Array.Empty<ProviderDescriptor>()
            : new List<ProviderDescriptor>(providers);
        _projectPath = projectPath ?? string.Empty;
        _appVersion = string.IsNullOrWhiteSpace(appVersion) ? "0.0.0" : appVersion;
        // 只有真正被调用 Handler 使用的 Provider 才写入审计；由组合根传入
        // 实际 Provider 描述符的 ID，缺省为空，绝不在网关内按品牌或操作名推断。
        _executionProviderId = executionProviderId ?? string.Empty;
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
            // 快照来自组合根实际装配的 Provider；Application 不根据品牌 ID 推断能力。
            Providers = new List<ProviderDescriptor>(_providers),
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
        // 输入摘要只计算一次：Activity 预览与 Artifact 内容共用同一份脱敏结果。
        var inputSummary = ActivityContentPolicy.Summarize(request.InputJson, "input");
        var record = ActivityRecordFactory.CreateStarted(activity, request, DateTimeOffset.UtcNow, inputSummary);

        var dispatch = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);

        await TryRecordActivityAsync(record, dispatch.Result, dispatch.ProviderId, inputSummary).ConfigureAwait(false);

        return dispatch.Result;
    }

    /// <summary>按目录、Handler、运行上下文顺序分发；所有分支都返回结构化结果。</summary>
    private async Task<(OperationResult Result, string ProviderId)> DispatchAsync(
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        var descriptor = _catalog.Find(request.OperationId);
        if (descriptor is null)
        {
            return (Failure(
                ErrorCodes.NotFound,
                "catalog",
                $"操作目录中不存在：{request.OperationId}",
                new Dictionary<string, string> { ["operationId"] = request.OperationId }),
                string.Empty);
        }

        var handler = _handlers.Find(request.OperationId);
        if (handler is null
            || !descriptor.Implemented
            || !string.Equals(descriptor.Availability, OperationAvailability.Available, StringComparison.Ordinal))
        {
            return (Unsupported(
                request.OperationId,
                "当前构建未注册该操作的 Handler。",
                descriptor.Milestone),
                string.Empty);
        }

        var factory = _contextFactory;
        var context = factory?.TryCreate(request);
        if (context is null)
        {
            return (Unsupported(
                request.OperationId,
                "运行环境未装配 Workspace/Editor/Artifact 基础设施。",
                descriptor.Milestone),
                string.Empty);
        }

        // 目录能力必须由真实 Provider 快照或已注册操作的显式能力满足。未知能力一律拒绝。
        CapabilitySnapshot capabilitySnapshot;
        try
        {
            capabilitySnapshot = await _capabilities.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (Failure(ErrorCodes.Cancelled, "capability", "调用已取消。", status: OperationStatus.Cancelled),
                string.Empty);
        }
        catch (Exception exception)
        {
            return (Failure(
                ErrorCodes.ExecutionFailed,
                "capability",
                "能力快照读取失败。",
                new Dictionary<string, string> { ["exceptionType"] = exception.GetType().Name }),
                string.Empty);
        }

        var missingCapabilities = descriptor.RequiredCapabilities
            .Where(capability => !capabilitySnapshot.Capabilities.TryGetValue(capability, out var available) || !available)
            .ToList();
        if (missingCapabilities.Count > 0)
        {
            return (Failure(
                ErrorCodes.CapabilityUnsupported,
                "capability",
                "当前 Provider 缺少操作所需能力：" + string.Join(", ", missingCapabilities) + "。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["operationId"] = request.OperationId,
                    ["missingCapabilities"] = string.Join(",", missingCapabilities)
                },
                OperationStatus.Unsupported),
                string.Empty);
        }

        // 目录值是硬上限；请求只可缩短，不能延长。TimeSpan 与 CancelAfter 均避免毫秒乘法溢出。
        var catalogTimeout = descriptor.TimeoutSeconds > 0 ? descriptor.TimeoutSeconds : 60;
        var requestedTimeout = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : catalogTimeout;
        var effectiveSeconds = Math.Min((long)requestedTimeout, (long)catalogTimeout);
        effectiveSeconds = Math.Clamp(effectiveSeconds, 1L, MaxCancellationDelaySeconds);

        // 到这里 Handler 将被真实调用。按目录声明能力选择实际 Provider，
        // 不根据品牌 ID 或操作字符串分支。
        var providerId = ResolveExecutionProviderId(descriptor);

        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        executionCts.CancelAfter(TimeSpan.FromSeconds(effectiveSeconds));
        try
        {
            var result = await handler.ExecuteAsync(request, context, executionCts.Token).ConfigureAwait(false);
            result ??= Failure(ErrorCodes.InternalError, "execution", "Handler 返回了空结果。");

            // Handler 会统一捕获 OperationCanceledException；这里根据令牌来源纠正为真实超时。
            if (!cancellationToken.IsCancellationRequested
                && executionCts.IsCancellationRequested
                && result.Status == OperationStatus.Cancelled)
            {
                return (TimeoutFailure(effectiveSeconds), providerId);
            }

            return (result, providerId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (Failure(ErrorCodes.Cancelled, "execution", "调用已取消。", status: OperationStatus.Cancelled),
                providerId);
        }
        catch (OperationCanceledException) when (executionCts.IsCancellationRequested)
        {
            return (TimeoutFailure(effectiveSeconds), providerId);
        }
        catch (WorkspaceException exception)
        {
            // Handler 漏捕 Provider 异常时仍保留稳定错误码，不能折叠为 EXECUTION_FAILED。
            return (Failure(
                exception.Code,
                "workspace",
                exception.Message,
                exception.Details.Count == 0
                    ? null
                    : new Dictionary<string, string>(exception.Details, StringComparer.Ordinal),
                string.Equals(exception.Code, ErrorCodes.Cancelled, StringComparison.Ordinal)
                    ? OperationStatus.Cancelled
                    : OperationStatus.Failed),
                providerId);
        }
        catch (EditorException exception)
        {
            // Provider 已把上游进程失败转换为稳定错误码；网关必须保留 Code/Details，
            // 否则调用方无法区分“编辑器未就绪 / 工具缺失 / 上游工具失败”，
            // 也会让 Editor 的失败语义被折叠成统一的 EXECUTION_FAILED。
            return (Failure(
                exception.Code,
                "editor",
                exception.Message,
                exception.Details.Count == 0
                    ? null
                    : new Dictionary<string, string>(exception.Details, StringComparer.Ordinal),
                string.Equals(exception.Code, ErrorCodes.Cancelled, StringComparison.Ordinal)
                    ? OperationStatus.Cancelled
                    : OperationStatus.Failed),
                providerId);
        }
        catch (Exception exception)
        {
            return (Failure(
                ErrorCodes.ExecutionFailed,
                "execution",
                "Handler 执行时发生未处理异常。",
                new Dictionary<string, string> { ["exceptionType"] = exception.GetType().Name }),
                providerId);
        }
    }

    /// <summary>CancellationTokenSource.CancelAfter 支持的保守秒数上限。</summary>
    private const long MaxCancellationDelaySeconds = 4_000_000L;

    private static OperationResult TimeoutFailure(long effectiveSeconds)
    {
        return Failure(
            ErrorCodes.Timeout,
            "execution",
            $"操作超过有效超时 {effectiveSeconds} 秒。",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["timeoutSeconds"] = effectiveSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
    }

    /// <summary>
    /// 补全并写入 Activity。任何审计侧故障都只产生 Warning，不改变业务结果。
    /// 设计边界：M1 只在终态写一次完整记录，不额外持久化 Started 快照，
    /// 避免同一 ActivityId 的两次独立写入在崩溃时留下无法原子区分的中间状态。
    /// </summary>
    private async Task TryRecordActivityAsync(
        ActivityRecord record,
        OperationResult result,
        string providerId,
        ActivityPayloadSummary inputSummary)
    {
        // Activity/Artifact 都位于绑定项目内；项目根不存在时写入会隐式创建目录，
        // 把一个不存在的项目伪装成可审计的成功调用。这里直接跳过并给出稳定告警。
        if (_projectPath.Length == 0 || !Directory.Exists(_projectPath))
        {
            AddWarning(
                result,
                AuditUnavailableProjectMissing,
                "绑定项目根不存在，已跳过 Activity 审计。",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["projectPath"] = _projectPath });
            return;
        }

        record.ProviderId = providerId ?? string.Empty;

        // 先落 Artifact 再写 Activity：Artifact 失败只追加 Warning，
        // Activity 仍保留带有 $artifact 标记与哈希的摘要供调用方识别。
        record.Input.Artifact = await TryWriteArtifactAsync(inputSummary, record, "input", result).ConfigureAwait(false);
        var outputSummary = ActivityContentPolicy.Summarize(
            ActivityRecordFactory.SerializeForPreview(result),
            "output");

        // 输出 Artifact 写入可能追加 Warning（写失败/校验不一致）。
        // 正常成功路径直接复用同一份摘要，保证 PreviewJson 的哈希与 Artifact 完全一致；
        // 只有结果确被追加 Warning 时才重新计算终态预览，避免旧摘要丢掉诊断。
        var warningsBeforeOutputArtifact = result.Warnings.Count;
        var outputArtifact = await TryWriteArtifactAsync(outputSummary, record, "output", result).ConfigureAwait(false);
        var finalOutputSummary = result.Warnings.Count == warningsBeforeOutputArtifact
            ? outputSummary
            : ActivityContentPolicy.Summarize(
                ActivityRecordFactory.SerializeForPreview(result),
                "output");
        ActivityRecordFactory.Complete(
            record,
            result,
            (int)ExitCodePolicy.Map(result),
            DateTimeOffset.UtcNow,
            finalOutputSummary);
        // Complete 会重建 Output 载荷，Artifact 引用必须在终态之后挂回。
        record.Output.Artifact = outputArtifact;

        try
        {
            // 审计写入使用独立令牌，避免调用方取消导致记录只停留在 Running。
            await _activitySink.RecordAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AddWarning(
                result,
                "ACTIVITY_WRITE_FAILED",
                "Activity 写入失败，业务结果仍然有效。",
                new Dictionary<string, string> { ["exceptionType"] = exception.GetType().Name });
        }
    }

    /// <summary>
    /// 大内容写入项目 Artifact Store，并把可恢复引用挂到 Activity 载荷上。
    /// 写入内容来自 ActivityContentPolicy 的脱敏结果，不会绕过脱敏把 secret 原样落盘。
    /// </summary>
    private async Task<ArtifactReference?> TryWriteArtifactAsync(
        ActivityPayloadSummary summary,
        ActivityRecord record,
        string payloadKind,
        OperationResult result)
    {
        if (!summary.ArtifactRequired)
        {
            return null;
        }

        if (_artifacts is null)
        {
            AddWarning(
                result,
                ActivityArtifactUnavailable,
                "检测到大内容，但当前运行环境未装配 Artifact Store，仅保留摘要。",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["kind"] = payloadKind });
            return null;
        }

        try
        {
            var reference = await _artifacts.WriteAsync(
                new ArtifactWriteRequest
                {
                    ProjectPath = _projectPath,
                    FileName = record.ActivityId + "-" + payloadKind
                        + (string.Equals(summary.ArtifactContentType, "application/json", StringComparison.OrdinalIgnoreCase)
                            ? ".json"
                            : ".txt"),
                    ContentType = summary.ArtifactContentType,
                    Content = Encoding.UTF8.GetBytes(summary.ArtifactContent),
                    SourceActivityId = record.ActivityId
                },
                CancellationToken.None).ConfigureAwait(false);

            // 摘要中的哈希/大小必须能验证真实落盘内容，否则调用方无法信任 Artifact。
            if (reference.SizeBytes != summary.ArtifactBytes
                || !string.Equals(reference.Sha256, summary.ArtifactSha256, StringComparison.OrdinalIgnoreCase))
            {
                AddWarning(
                    result,
                    ActivityArtifactVerifyFailed,
                    "Artifact 落盘后的大小或哈希与摘要不一致。",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["kind"] = payloadKind,
                        ["expectedBytes"] = summary.ArtifactBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["actualBytes"] = reference.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["expectedSha256"] = summary.ArtifactSha256,
                        ["actualSha256"] = reference.Sha256
                    });

                // 校验失败的文件保留在磁盘上供诊断，但本次 Activity 不得挂载不可信引用：
                // 否则调用方按引用读取时会拿到与摘要不符的内容，且无法从 Activity 本身识别该风险。
                // 这里不删除已写文件，避免在审计路径引入额外的破坏性操作。
                return null;
            }

            return reference;
        }
        catch (Exception exception)
        {
            AddWarning(
                result,
                ActivityArtifactWriteFailed,
                "Artifact 写入失败，仅保留 Activity 摘要，业务结果不变。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kind"] = payloadKind,
                    ["exceptionType"] = exception.GetType().Name
                });
            return null;
        }
    }

    private static void AddWarning(
        OperationResult result,
        string code,
        string message,
        Dictionary<string, string>? details = null)
    {
        result.Warnings.Add(new KernelWarning
        {
            Code = code,
            Message = message,
            Details = details ?? new Dictionary<string, string>()
        });
    }

    /// <summary>
    /// 优先选择能声明全部必需能力的 Provider；没有能力要求时使用组合根默认值。
    /// 多个 Provider 同时满足时保持 Composition 输入顺序，避免隐式优先级。
    /// </summary>
    private string ResolveExecutionProviderId(OperationDescriptor descriptor)
    {
        if (_providers.Count == 0)
        {
            return _executionProviderId;
        }

        var candidates = _providers.Where(provider =>
            descriptor.RequiredCapabilities.All(required =>
                provider.Capabilities.TryGetValue(required, out var available) && available));
        var preferred = candidates.FirstOrDefault(provider =>
            provider.Kind is not null && descriptor.Environment.Contains(provider.Kind));
        return preferred?.Id ?? _executionProviderId;
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
