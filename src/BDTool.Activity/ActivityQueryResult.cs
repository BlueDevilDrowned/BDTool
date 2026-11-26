using System;
using System.Collections.Generic;
using BDTool.Contracts.Activity;

namespace BDTool.Activity;

/// <summary>
/// 带诊断信息的 Activity 查询结果。损坏文件被跳过而不是让查询失败，
/// 调用方可以据此提示数据修复，而不必伪造一条“正常”记录。
/// </summary>
public sealed class ActivityQueryResult
{
    public ActivityQueryResult(
        IReadOnlyList<ActivitySummary> summaries,
        IReadOnlyList<ActivityReadFailure> unreadableFiles)
    {
        Summaries = summaries ?? throw new ArgumentNullException(nameof(summaries));
        UnreadableFiles = unreadableFiles ?? throw new ArgumentNullException(nameof(unreadableFiles));
    }

    /// <summary>已应用过滤、排序与分页的摘要列表。</summary>
    public IReadOnlyList<ActivitySummary> Summaries { get; }

    /// <summary>本次查询中无法解析的 Activity 文件。</summary>
    public IReadOnlyList<ActivityReadFailure> UnreadableFiles { get; }
}

/// <summary>无法读取的 Activity 文件及原因；原因只含异常类型与消息，不含文件内容。</summary>
public sealed class ActivityReadFailure
{
    public ActivityReadFailure(string filePath, string reason)
    {
        FilePath = filePath ?? string.Empty;
        Reason = reason ?? string.Empty;
    }

    /// <summary>无法读取的完整文件路径。</summary>
    public string FilePath { get; }

    /// <summary>可读的失败原因，用于诊断，不含敏感内容。</summary>
    public string Reason { get; }
}
