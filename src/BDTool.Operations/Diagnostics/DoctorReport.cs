using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>按设计明确跳过，不视为失败；例如未装配 Editor Provider 或 Provider 不支持状态查询。</summary>
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
/// doctor 的 typed 结果。描述应用自身、项目目录、存储环境、Provider 与真实
/// Editor 状态；未装配 Editor Provider 时明确标为未连接。
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

    /// <summary>当前真实装配的 Provider 快照；M1 预期包含 local Workspace Provider。</summary>
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
/// Unity 连接报告。只有真实 Provider 报告 ready、且报告的项目路径与请求项目一致时，
/// 才允许 Connected=true；未装配、超时或路径不一致都必须保持未连接。
/// </summary>
public sealed class UnityConnectionReport
{
    public bool Connected { get; set; }
    public string Integration { get; set; } = "not-assembled";
    public string Detail { get; set; } = "当前组合根未装配 Editor Provider。";
}

/// <summary>
/// Editor 报告的项目路径与请求项目是否指向同一目录。任一侧为空都视为不匹配：
/// “无法确认”不能当成“已连接”，否则多开 Editor 或上游省略字段时会误报。
/// Windows 路径大小写不敏感，其他平台按序数比较；末尾分隔符与相对路径先规范化。
/// </summary>
internal static class EditorProjectMatch
{
    public static bool Matches(string? requestedProjectPath, string? reportedProjectPath)
    {
        if (string.IsNullOrWhiteSpace(requestedProjectPath)
            || string.IsNullOrWhiteSpace(reportedProjectPath))
        {
            return false;
        }

        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(
                Normalize(requestedProjectPath),
                Normalize(reportedProjectPath),
                comparison);
        }
        catch (Exception)
        {
            // 非法/超长路径无法证明是同一项目，按不匹配处理，由上层给出可见诊断。
            return false;
        }
    }

    /// <summary>转成绝对路径并去掉末尾分隔符，但保留盘符根（如 C:\）。</summary>
    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full) ?? string.Empty;
        return full.Length > root.Length
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }
}
