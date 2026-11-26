using System.Collections.Generic;
using BDTool.Contracts.Providers;

namespace BDTool.Operations.Diagnostics;

/// <summary>
/// doctor 检查项的稳定状态值。使用字符串而不是枚举，保证公共 JSON 输出
/// 不随内部类型调整而变化。
/// </summary>
public static class DoctorCheckStatuses
{
    public const string Pass = "pass";
    public const string Warn = "warn";
    public const string Fail = "fail";

    /// <summary>按设计明确跳过，不视为失败；例如 M0 不连接 Unity。</summary>
    public const string Skipped = "skipped";
}

/// <summary>doctor 汇总状态。ready 表示应用自身就绪，不代表 Unity 可用。</summary>
public static class DoctorStates
{
    public const string Ready = "ready";
    public const string Degraded = "degraded";
    public const string NotReady = "not-ready";
}

/// <summary>
/// M0 doctor 的 typed 结果。只描述应用自身、项目目录与存储环境；
/// Unity 连接状态固定为未连接，禁止据此推断任何 Unity 兼容性。
/// </summary>
public sealed class DoctorReport
{
    public string State { get; set; } = DoctorStates.NotReady;
    public string AppName { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public string ContractVersion { get; set; } = string.Empty;
    public string Milestone { get; set; } = string.Empty;
    public string ProjectPath { get; set; } = string.Empty;
    public string StateRoot { get; set; } = string.Empty;
    public string ActivityRoot { get; set; } = string.Empty;
    public List<DoctorCheck> Checks { get; set; } = new List<DoctorCheck>();

    /// <summary>M0 没有装配任何 Provider，必须保持为空列表。</summary>
    public List<ProviderDescriptor> Providers { get; set; } = new List<ProviderDescriptor>();

    public UnityConnectionReport Unity { get; set; } = new UnityConnectionReport();
    public string Note { get; set; } = string.Empty;
}

/// <summary>单个 doctor 检查项。Detail 面向人类排障，不包含堆栈或敏感信息。</summary>
public sealed class DoctorCheck
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = DoctorCheckStatuses.Fail;
    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// Unity 连接报告。M0 固定 Connected=false；Editor Provider 属于 M2，
/// 任何“可用”声明都必须等真实项目与 Editor 验证。
/// </summary>
public sealed class UnityConnectionReport
{
    public bool Connected { get; set; }
    public string Integration { get; set; } = "not-implemented";
    public string Detail { get; set; } = "M0 不连接 Unity；Editor Provider 尚未实现。";
}
