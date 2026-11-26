using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;
using BDTool.Core;
using BDTool.Operations.Handlers;
using BDTool.Operations.Input;

namespace BDTool.Operations.Diagnostics;

/// <summary>
/// system.doctor 实现。检查应用自身、项目目录、Activity 存储与 Provider 快照；
/// 装配 Editor Provider 时读取真实 Editor 状态，未装配时明确标记为 skipped。
/// </summary>
public sealed class DoctorHandler : IOperationHandler
{
    /// <summary>目录中冻结的 doctor 操作 ID。</summary>
    public const string StableOperationId = "system.doctor";

    /// <summary>当前运行时里程碑；值来自 Core 产品身份，避免 Doctor 与 --version 漂移。</summary>
    public const string CurrentMilestone = BDToolIdentity.Milestone;

    /// <summary>Provider 可用性检查的稳定 ID，失败时用于把诊断字段挂到正确检查上。</summary>
    private const string ProviderCheckId = "providers.available";

    /// <summary>M1 要求 Workspace Provider 上报的四项能力，缺失任一项都视为描述不完整。</summary>
    private static readonly string[] RequiredWorkspaceCapabilities =
    {
        CapabilityIds.WorkspaceRead,
        CapabilityIds.WorkspaceWrite,
        CapabilityIds.WorkspaceSearch,
        CapabilityIds.WorkspaceFingerprint
    };

    private readonly IDoctorEnvironmentProbe _probe;
    private readonly IOperationCatalog _catalog;
    private readonly string _appVersion;
    private readonly string _defaultProjectPath;
    private readonly IReadOnlyList<ProviderDescriptor> _providers;
    private readonly IEditorKernel? _editor;

    /// <summary>
    /// 依赖由组合根显式注入：环境探测在本机执行，目录用于确认 doctor 自身已登记，
    /// Provider 列表必须反映当前真实装配结果（M1 包含 Local Workspace Provider）。
    /// </summary>
    public DoctorHandler(
        IDoctorEnvironmentProbe probe,
        IOperationCatalog catalog,
        string appVersion,
        string? defaultProjectPath = null,
        IReadOnlyList<ProviderDescriptor>? providers = null,
        IEditorKernel? editor = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _appVersion = appVersion ?? string.Empty;
        _defaultProjectPath = defaultProjectPath ?? string.Empty;
        _providers = providers ?? Array.Empty<ProviderDescriptor>();
        _editor = editor;
    }

    /// <inheritdoc />
    public string OperationId => StableOperationId;

    /// <inheritdoc />
    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        IOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // system.doctor 的 inputSchema 是严格空对象（additionalProperties=false），
        // Handler 必须做同样的校验：任何输入属性都说明调用方误解了操作契约，
        // 静默忽略会把拼写错误或过时字段当成成功执行。
        try
        {
            using var input = OperationInputParser.ParseObject(request.InputJson);
        }
        catch (OperationInputException exception)
        {
            return OperationResults.Failure<DoctorReport>(
                ErrorCodes.InvalidArgument,
                "input",
                exception.Message);
        }

        // 请求未携带项目路径时（例如进程内调用）使用组合根给出的默认项目。
        var projectPath = string.IsNullOrWhiteSpace(request.ProjectPath)
            ? _defaultProjectPath
            : request.ProjectPath;

        var snapshot = await _probe.ProbeAsync(projectPath, cancellationToken).ConfigureAwait(false);
        var (report, providerAudit) = await BuildReportAsync(snapshot, projectPath, cancellationToken)
            .ConfigureAwait(false);
        var result = new OperationResult<DoctorReport> { Data = report };

        var failed = report.Checks.FindAll(check => check.Status == DoctorCheckStatuses.Fail);
        if (failed.Count == 0)
        {
            result.Status = OperationStatus.Completed;
            return result;
        }

        result.Status = OperationStatus.Failed;
        var projectMissing = failed.Exists(check => check.Id == "project.path");
        result.Errors.Add(new KernelError
        {
            Code = projectMissing ? ErrorCodes.ProjectNotFound : ErrorCodes.EnvironmentUnsupported,
            Stage = "doctor",
            Message = projectMissing
                ? "项目路径不存在，doctor 无法完成检查。"
                : "doctor 检查未通过：" + failed[0].Title + "。",
            Retryable = false,
            Details = BuildFailureDetails(failed[0], report, providerAudit)
        });
        return result;
    }

    /// <summary>
    /// 把探测快照映射为逐项检查。状态汇总只看 fail/warn：
    /// 任一 fail 为 not-ready，仅有 warn 为 degraded，其余为 ready。
    /// </summary>
    private async Task<(DoctorReport Report, ProviderAudit ProviderAudit)> BuildReportAsync(
        DoctorEnvironmentSnapshot snapshot,
        string requestedProjectPath,
        CancellationToken cancellationToken)
    {
        var catalogCount = _catalog.GetAll().Count;
        var doctorRegistered = _catalog.Find(StableOperationId) is not null;

        var report = new DoctorReport
        {
            AppName = BDToolIdentity.ProductName,
            AppVersion = _appVersion,
            ContractVersion = BDToolContract.Version,
            Milestone = CurrentMilestone,
            ProjectPath = snapshot.ProjectPath,
            StateRoot = snapshot.StateRoot,
            ActivityRoot = snapshot.ActivityRoot,
            Providers = new List<ProviderDescriptor>(_providers),
            Unity = new UnityConnectionReport(),
            Note = "检查应用自身、项目目录、Activity 存储与真实 Provider；Editor 状态只在 Editor Provider 装配后读取。"
        };

        report.Checks.Add(Check(
            "app.version",
            "应用版本",
            string.IsNullOrWhiteSpace(_appVersion) ? DoctorCheckStatuses.Fail : DoctorCheckStatuses.Pass,
            string.IsNullOrWhiteSpace(_appVersion) ? "应用版本为空。" : "应用版本 " + _appVersion));

        report.Checks.Add(Check(
            "contract.version",
            "契约版本",
            string.IsNullOrWhiteSpace(BDToolContract.Version) ? DoctorCheckStatuses.Fail : DoctorCheckStatuses.Pass,
            "契约版本 " + BDToolContract.Version));

        report.Checks.Add(Check(
            "project.path",
            "项目路径",
            snapshot.ProjectExists ? DoctorCheckStatuses.Pass : DoctorCheckStatuses.Fail,
            snapshot.ProjectDetail));

        report.Checks.Add(Check(
            "state.root",
            "状态目录可写",
            snapshot.StateRootWritable ? DoctorCheckStatuses.Pass : DoctorCheckStatuses.Fail,
            snapshot.StateRootDetail));

        report.Checks.Add(Check(
            "activity.root",
            "Activity 目录可写",
            snapshot.ActivityRootWritable ? DoctorCheckStatuses.Pass : DoctorCheckStatuses.Fail,
            snapshot.ActivityRootDetail));

        report.Checks.Add(Check(
            "catalog.loaded",
            "操作目录已加载",
            doctorRegistered ? DoctorCheckStatuses.Pass : DoctorCheckStatuses.Fail,
            doctorRegistered
                ? "已加载 " + catalogCount + " 个操作，包含 " + StableOperationId + "。"
                : "操作目录缺少 " + StableOperationId + "。"));

        var providerValidation = ValidateProviders();
        var providerAudit = providerValidation.Audit;
        report.Checks.Add(providerValidation.Check);

        if (_editor is null)
        {
            report.Unity = new UnityConnectionReport
            {
                Connected = false,
                Integration = "not-assembled",
                Detail = "当前组合根未装配 Editor Provider。"
            };
            report.Checks.Add(Check(
                "unity.connection",
                "Unity 连接",
                DoctorCheckStatuses.Skipped,
                report.Unity.Detail));
        }
        else
        {
            try
            {
                var editorStatus = await _editor.GetStatusAsync(
                    new EditorStatusRequest { ProjectPath = requestedProjectPath },
                    cancellationToken).ConfigureAwait(false);
                // ready 只说明“某个 Editor 在线”；必须同时确认它挂载的是请求项目，
                // 否则多开 Editor 或默认项目会把别的编辑器误报成当前项目已连接。
                var projectMatches = EditorProjectMatch.Matches(
                    requestedProjectPath,
                    editorStatus.ProjectPath);
                var connected = string.Equals(editorStatus.State, "ready", StringComparison.Ordinal)
                    && projectMatches;
                report.Unity = new UnityConnectionReport
                {
                    Connected = connected,
                    Integration = editorStatus.State,
                    Detail = BuildEditorDetail(editorStatus, requestedProjectPath, projectMatches)
                };
                report.Checks.Add(Check(
                    "unity.connection",
                    "Unity 连接",
                    connected ? DoctorCheckStatuses.Pass : DoctorCheckStatuses.Warn,
                    report.Unity.Detail));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException exception)
            {
                // 超时是稳定状态而不是通用失败：保留 integration=timeout 供调用方区分重试。
                report.Unity = new UnityConnectionReport
                {
                    Connected = false,
                    Integration = "timeout",
                    Detail = "Editor 状态查询超时：" + exception.Message
                };
                report.Checks.Add(Check(
                    "unity.connection",
                    "Unity 连接",
                    DoctorCheckStatuses.Fail,
                    report.Unity.Detail));
            }
            catch (EditorException exception)
            {
                report.Unity = new UnityConnectionReport
                {
                    Connected = false,
                    Integration = "error",
                    Detail = exception.Code + ": " + exception.Message
                };
                report.Checks.Add(Check(
                    "unity.connection",
                    "Unity 连接",
                    DoctorCheckStatuses.Fail,
                    report.Unity.Detail));
            }
            catch (NotSupportedException)
            {
                report.Unity = new UnityConnectionReport
                {
                    Connected = false,
                    Integration = "not-supported",
                    Detail = "Editor Provider 不支持状态查询。"
                };
                report.Checks.Add(Check(
                    "unity.connection",
                    "Unity 连接",
                    DoctorCheckStatuses.Skipped,
                    report.Unity.Detail));
            }
        }

        if (report.Checks.Exists(check => check.Status == DoctorCheckStatuses.Fail))
        {
            report.State = DoctorStates.NotReady;
        }
        else if (report.Checks.Exists(check => check.Status == DoctorCheckStatuses.Warn))
        {
            report.State = DoctorStates.Degraded;
        }
        else
        {
            report.State = DoctorStates.Ready;
        }

        return (report, providerAudit);
    }

    private static string BuildEditorDetail(
        EditorStatus status,
        string requestedProjectPath,
        bool projectMatches)
    {
        var detail = "state=" + status.State;
        if (!projectMatches)
        {
            detail += string.IsNullOrWhiteSpace(status.ProjectPath)
                ? "; projectPath=<empty>（Provider 未报告项目，无法确认连接属于当前项目）"
                : "; projectPath=" + status.ProjectPath + "（与请求项目 " + requestedProjectPath + " 不一致）";
        }

        if (!string.IsNullOrWhiteSpace(status.UnityVersion))
        {
            detail += "; unity=" + status.UnityVersion;
        }

        if (status.ProcessId is int processId)
        {
            detail += "; pid=" + processId.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(status.Message))
        {
            detail += "; " + status.Message;
        }

        return detail;
    }

    /// <summary>
    /// 构造失败诊断详情。Provider 检查失败时补充稳定的机器可读字段；
    /// 其余检查沿用 checkId/projectPath，避免调用方依赖自由文本排障。
    /// </summary>
    private static Dictionary<string, string> BuildFailureDetails(
        DoctorCheck failedCheck,
        DoctorReport report,
        ProviderAudit providerAudit)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["checkId"] = failedCheck.Id,
            ["projectPath"] = report.ProjectPath
        };

        // 只有 Provider 检查本身失败时才附加 Provider 字段，防止把首个失败的
        // 其他检查（例如 project.path）误标成 Provider 问题。
        if (!providerAudit.HasFailure
            || !string.Equals(failedCheck.Id, ProviderCheckId, StringComparison.Ordinal))
        {
            return details;
        }

        details["reason"] = providerAudit.ReasonCode;
        details["providerIndex"] = providerAudit.ProviderIndex.ToString(CultureInfo.InvariantCulture);
        details["providerId"] = providerAudit.ProviderId;
        details["providerCount"] = providerAudit.ProviderCount.ToString(CultureInfo.InvariantCulture);
        details["expectedContractVersion"] = BDToolContract.Version;
        details["actualContractVersion"] = providerAudit.ActualContractVersion;
        if (providerAudit.MissingCapabilities.Length > 0)
        {
            details["missingCapabilities"] = providerAudit.MissingCapabilities;
        }

        return details;
    }

    /// <summary>
    /// 校验真实装配的 Provider 描述，不按品牌分支。硬性条件：ID 唯一且非空、
    /// Kind 属于已冻结类别、契约版本一致、实现版本非空；Workspace Provider
    /// 还必须上报四项能力为 true。DisplayName 缺失只降级为 warn，因为它是
    /// 展示字段，不影响能力判断，但必须让调用方看到描述不完整。
    /// 空列表保持 M1 既有的 degraded 语义：应用自身可运行，但没有可执行 Provider。
    /// </summary>
    private ProviderValidation ValidateProviders()
    {
        if (_providers.Count == 0)
        {
            return new ProviderValidation(
                Check(
                    ProviderCheckId,
                    "Provider 可用性",
                    DoctorCheckStatuses.Warn,
                    "当前构建未装配 Provider。"),
                default);
        }

        var segments = new List<string>(_providers.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var anyWarning = false;
        var workspaceProviderFound = false;
        var audit = default(ProviderAudit);

        for (var index = 0; index < _providers.Count; index++)
        {
            var provider = _providers[index];
            if (provider is null)
            {
                // 组合根正常不会传 null；这里仍给出稳定原因，避免空引用掩盖装配缺陷。
                segments.Add(
                    "provider[" + index.ToString(CultureInfo.InvariantCulture) +
                    "]{status=fail; reasons=PROVIDER_DESCRIPTOR_NULL}");
                if (!audit.HasFailure)
                {
                    audit = new ProviderAudit(
                        "PROVIDER_DESCRIPTOR_NULL",
                        index,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        _providers.Count);
                }

                continue;
            }

            var providerId = provider.Id is null ? string.Empty : provider.Id;
            var kind = provider.Kind is null ? string.Empty : provider.Kind;
            var contractVersion = provider.ContractVersion is null ? string.Empty : provider.ContractVersion;
            var implementationVersion = provider.ImplementationVersion is null ? string.Empty : provider.ImplementationVersion;
            var displayName = provider.DisplayName is null ? string.Empty : provider.DisplayName;

            var reasons = new List<string>();
            var warnings = new List<string>();
            var missingCapabilities = new List<string>();

            if (string.IsNullOrWhiteSpace(providerId))
            {
                reasons.Add("PROVIDER_ID_EMPTY");
            }
            else if (!seenIds.Add(providerId))
            {
                reasons.Add("PROVIDER_ID_DUPLICATE");
            }

            var kindValid = IsKnownProviderKind(kind);
            if (!kindValid)
            {
                reasons.Add("PROVIDER_KIND_INVALID");
            }

            if (!string.Equals(contractVersion, BDToolContract.Version, StringComparison.Ordinal))
            {
                reasons.Add("PROVIDER_CONTRACT_MISMATCH");
            }

            if (string.IsNullOrWhiteSpace(implementationVersion))
            {
                reasons.Add("PROVIDER_IMPLEMENTATION_VERSION_EMPTY");
            }

            if (string.Equals(kind, ProviderKinds.Workspace, StringComparison.Ordinal))
            {
                workspaceProviderFound = true;
                CollectMissingCapabilities(provider.Capabilities, missingCapabilities);
                if (missingCapabilities.Count > 0)
                {
                    reasons.Add("PROVIDER_CAPABILITIES_MISSING");
                }
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                warnings.Add("PROVIDER_DISPLAY_NAME_EMPTY");
                anyWarning = true;
            }

            var providerStatus = reasons.Count > 0
                ? DoctorCheckStatuses.Fail
                : warnings.Count > 0
                    ? DoctorCheckStatuses.Warn
                    : DoctorCheckStatuses.Pass;

            segments.Add(BuildProviderSegment(
                index,
                providerId,
                kind,
                kindValid,
                contractVersion,
                implementationVersion,
                displayName,
                provider.Capabilities,
                missingCapabilities,
                providerStatus,
                reasons,
                warnings));

            if (reasons.Count > 0 && !audit.HasFailure)
            {
                audit = new ProviderAudit(
                    reasons[0],
                    index,
                    providerId,
                    contractVersion,
                    string.Join(",", missingCapabilities),
                    _providers.Count);
            }
        }

        // M1 的可用性来自 Workspace Provider；只有 Editor Provider 时不能报告 ready。
        if (!workspaceProviderFound && !audit.HasFailure)
        {
            audit = new ProviderAudit(
                "PROVIDER_WORKSPACE_MISSING",
                -1,
                string.Empty,
                string.Empty,
                string.Empty,
                _providers.Count);
            segments.Add("workspaceProvider=none{status=fail; reasons=PROVIDER_WORKSPACE_MISSING}");
        }

        var status = audit.HasFailure
            ? DoctorCheckStatuses.Fail
            : anyWarning
                ? DoctorCheckStatuses.Warn
                : DoctorCheckStatuses.Pass;

        return new ProviderValidation(
            Check(ProviderCheckId, "Provider 可用性", status, string.Join(" | ", segments)),
            audit);
    }

    /// <summary>四项 Workspace 能力逐项检查；能力字典为 null 时按四项全缺处理。</summary>
    private static void CollectMissingCapabilities(
        Dictionary<string, bool>? capabilities,
        List<string> missing)
    {
        foreach (var capability in RequiredWorkspaceCapabilities)
        {
            var enabled = capabilities is not null
                && capabilities.TryGetValue(capability, out var value)
                && value;
            if (!enabled)
            {
                missing.Add(capability);
            }
        }
    }

    private static bool IsKnownProviderKind(string kind) =>
        string.Equals(kind, ProviderKinds.Workspace, StringComparison.Ordinal)
        || string.Equals(kind, ProviderKinds.Editor, StringComparison.Ordinal);

    /// <summary>
    /// 生成固定键名的单行诊断段，便于人工排障与测试断言；
    /// 自由文本仅 DisplayName 一处，不参与状态判定。
    /// </summary>
    private static string BuildProviderSegment(
        int index,
        string providerId,
        string kind,
        bool kindValid,
        string contractVersion,
        string implementationVersion,
        string displayName,
        Dictionary<string, bool>? capabilities,
        List<string> missingCapabilities,
        string status,
        List<string> reasons,
        List<string> warnings)
    {
        var builder = new StringBuilder();
        builder.Append("provider[")
            .Append(index.ToString(CultureInfo.InvariantCulture))
            .Append("]{id=").Append(providerId)
            .Append("; kind=").Append(kind.Length == 0 ? "<empty>" : kind)
            .Append("; kindValid=").Append(kindValid ? "true" : "false")
            .Append("; contract=").Append(contractVersion.Length == 0 ? "<empty>" : contractVersion)
            .Append("; implementation=").Append(implementationVersion.Length == 0 ? "<empty>" : implementationVersion)
            .Append("; displayName=").Append(displayName.Length == 0 ? "<empty>" : displayName)
            .Append("; capabilities=");

        if (capabilities is null)
        {
            builder.Append("<null>");
        }
        else
        {
            for (var i = 0; i < RequiredWorkspaceCapabilities.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                var capability = RequiredWorkspaceCapabilities[i];
                var enabled = capabilities.TryGetValue(capability, out var value) && value;
                builder.Append(capability).Append('=').Append(enabled ? "true" : "false");
            }
        }

        builder.Append("; status=").Append(status);
        if (missingCapabilities.Count > 0)
        {
            builder.Append("; missing=").Append(string.Join(",", missingCapabilities));
        }

        if (reasons.Count > 0)
        {
            builder.Append("; reasons=").Append(string.Join(",", reasons));
        }

        if (warnings.Count > 0)
        {
            builder.Append("; warnings=").Append(string.Join(",", warnings));
        }

        builder.Append('}');
        return builder.ToString();
    }

    private static DoctorCheck Check(string id, string title, string status, string detail) =>
        new DoctorCheck { Id = id, Title = title, Status = status, Detail = detail };

    /// <summary>Provider 检查结果与失败定位信息，供报告和失败信封共用。</summary>
    private readonly struct ProviderValidation
    {
        public ProviderValidation(DoctorCheck check, ProviderAudit audit)
        {
            Check = check;
            Audit = audit;
        }

        public DoctorCheck Check { get; }
        public ProviderAudit Audit { get; }
    }

    /// <summary>
    /// Provider 校验的失败定位。只在硬性条件失败时填充；
    /// 成功或仅告警时保持 default，避免把告警误报为失败。
    /// </summary>
    private readonly struct ProviderAudit
    {
        public ProviderAudit(
            string reasonCode,
            int providerIndex,
            string providerId,
            string actualContractVersion,
            string missingCapabilities,
            int providerCount)
        {
            ReasonCode = reasonCode;
            ProviderIndex = providerIndex;
            ProviderId = providerId;
            ActualContractVersion = actualContractVersion;
            MissingCapabilities = missingCapabilities;
            ProviderCount = providerCount;
        }

        public string ReasonCode { get; }
        public int ProviderIndex { get; }
        public string ProviderId { get; }
        public string ActualContractVersion { get; }
        public string MissingCapabilities { get; }
        public int ProviderCount { get; }

        public bool HasFailure => !string.IsNullOrEmpty(ReasonCode);
    }
}
