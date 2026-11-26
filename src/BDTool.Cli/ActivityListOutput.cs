using System.Collections.Generic;
using BDTool.Contracts.Activity;

namespace BDTool.Cli;

/// <summary>
/// `bdtool activity list --json` 的协议输出。items 使用契约摘要，
/// count 是本次实际返回数量，不代表项目内 Activity 总数。
/// </summary>
public sealed class ActivityListOutput
{
    public List<ActivitySummary> Items { get; set; } = new List<ActivitySummary>();
    public int Count { get; set; }
}
