using System.Collections.Generic;
using BDTool.Contracts.Activity;
using BDTool.Contracts.Errors;

namespace BDTool.Cli;

/// <summary>
/// `bdtool activity list --json` 的协议输出。items 使用契约摘要，
/// count 是本次实际返回数量，不代表项目内 Activity 总数。
/// 无法解析的 Activity 文件不静默丢弃：unreadableFiles 给出结构化清单，
/// warnings 使用稳定告警码供机器消费，二者都不会包含文件内容。
/// </summary>
public sealed class ActivityListOutput
{
    public List<ActivitySummary> Items { get; set; } = new List<ActivitySummary>();
    public int Count { get; set; }
    public List<ActivityFileFailure> UnreadableFiles { get; set; } = new List<ActivityFileFailure>();
    public List<KernelWarning> Warnings { get; set; } = new List<KernelWarning>();
}
