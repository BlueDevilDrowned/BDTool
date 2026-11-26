using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;
using BDTool.Core;

namespace BDTool.Operations.Diagnostics;

/// <summary>
/// M0 的 system.doctor 实现。只检查应用自身、项目目录与 Activity 存储是否可用，
/// 不启动或连接 Unity；Unity 状态在结果中固定为未连接，避免把环境探测误报为集成成功。
/// </summary>
public sealed class DoctorHandler : IOperationHandler
{
    /// <summary>目录中冻结的 doctor 操作 ID。</summary>
    public const string StableOperationId = "system.doctor";

    private readonly IDoctorEnvironmentProbe _probe;
    private readonly IOperationCatalog _catalog;
    private readonly string _appVersion;
    private readonly string _defaultProjectPath;
    private readonly IReadOnlyList<ProviderDescriptor> _providers;

    /// <summary>
    /// 依赖由组合根显式注入：环境探测在本机执行，目录用于确认 doctor 自身已登记，
    /// Provider 列表必须反映当前真实装配结果（M0 为空）。
    /// </summary>
    public DoctorHandler(
        IDoctorEnvironmentProbe probe,
        IOperationCatalog catalog,
        string appVersion,
        string? defaultProjectPath = null,
        IReadOnlyList<ProviderDescriptor>? providers = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _appVersion = appVersion ?? string.Empty;
        _defaultProjectPath = defaultProjectPath ?? string.Empty;
        _providers = providers ?? Array.Empty<ProviderDescriptor>();
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

        // 请求未携带项目路径时（例如进程内调用）使用组合根给出的默认项目。
        var projectPath = string.IsNullOrWhiteSpace(request.ProjectPath)
            ? _defaultProjectPath
            : request.ProjectPath;

        var snapshot = await _probe.ProbeAsync(projectPath, cancellationToken).ConfigureAwait(false);
        var report = BuildReport(snapshot);
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
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["checkId"] = failed[0].Id,
                ["projectPath"] = report.ProjectPath
            }
        });
        return result;
    }

    /// <summary>
    /// 把探测快照映射为逐项检查。状态汇总只看 fail/warn：
    /// 任一 fail 为 not-ready，仅有 warn 为 degraded，其余为 ready。
    /// </summary>
    private DoctorReport BuildReport(DoctorEnvironmentSnapshot snapshot)
    {
        var catalogCount = _catalog.GetAll().Count;
        var doctorRegistered = _catalog.Find(StableOperationId) is not null;

        var report = new DoctorReport
        {
            AppName = BDToolIdentity.ProductName,
            AppVersion = _appVersion,
            ContractVersion = BDToolContract.Version,
            Milestone = BDToolIdentity.Milestone,
            ProjectPath = snapshot.ProjectPath,
            StateRoot = snapshot.StateRoot,
            ActivityRoot = snapshot.ActivityRoot,
            Providers = new List<ProviderDescriptor>(_providers),
            Unity = new UnityConnectionReport(),
            Note = "M0 仅验证应用自身、项目目录与 Activity 存储；不连接 Unity。"
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

        report.Checks.Add(Check(
            "providers.empty",
            "Provider 列表",
            _providers.Count == 0 ? DoctorCheckStatuses.Pass : DoctorCheckStatuses.Warn,
            _providers.Count == 0
                ? "当前构建未装配 Provider（0 个）。"
                : "当前构建装配了 " + _providers.Count + " 个 Provider；M0 预期为空。"));

        // M0 明确跳过 Unity：跳过不等于失败，但也不代表可用。
        report.Checks.Add(Check(
            "unity.connection",
            "Unity 连接",
            DoctorCheckStatuses.Skipped,
            report.Unity.Detail));

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

        return report;
    }

    private static DoctorCheck Check(string id, string title, string status, string detail) =>
        new DoctorCheck { Id = id, Title = title, Status = status, Detail = detail };
}
