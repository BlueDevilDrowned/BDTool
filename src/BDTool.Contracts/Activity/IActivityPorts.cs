using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BDTool.Contracts.Activity
{
    /// <summary>Activity 写入端口。实现负责原子写入，不接受多个进程追加同一文件。</summary>
    public interface IActivitySink
    {
        Task RecordAsync(ActivityRecord record, CancellationToken cancellationToken);
    }

    /// <summary>Activity 查询端口。UI 只能通过此端口读取，不得扫描任意日志目录。</summary>
    public interface IActivityQuery
    {
        Task<IReadOnlyList<ActivitySummary>> QueryAsync(ActivityQuery query, CancellationToken cancellationToken);
        Task<ActivityRecord> GetAsync(string activityId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 可选诊断查询端口：损坏的 Activity 文件被跳过时，调用方仍可拿到结构化失败清单。
    /// 只实现基础 IActivityQuery 的存储可以继续工作，CLI 会退化为无诊断输出。
    /// </summary>
    public interface IActivityDiagnosticsQuery
    {
        Task<ActivityQueryDiagnosticsResult> QueryWithDiagnosticsAsync(
            ActivityQuery query,
            CancellationToken cancellationToken);
    }

    /// <summary>带诊断信息的 Activity 查询结果。</summary>
    public sealed class ActivityQueryDiagnosticsResult
    {
        public ActivityQueryDiagnosticsResult(
            IReadOnlyList<ActivitySummary> summaries,
            IReadOnlyList<ActivityFileFailure> unreadableFiles)
        {
            Summaries = summaries ?? System.Array.Empty<ActivitySummary>();
            UnreadableFiles = unreadableFiles ?? System.Array.Empty<ActivityFileFailure>();
        }

        /// <summary>已应用过滤、排序与分页的摘要列表。</summary>
        public IReadOnlyList<ActivitySummary> Summaries { get; }

        /// <summary>本次查询中无法解析的 Activity 文件。</summary>
        public IReadOnlyList<ActivityFileFailure> UnreadableFiles { get; }
    }

    /// <summary>无法读取的 Activity 文件及原因；原因只含异常类型与消息，不含文件内容。</summary>
    public sealed class ActivityFileFailure
    {
        public ActivityFileFailure(string filePath, string reason)
        {
            FilePath = filePath ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        /// <summary>无法读取的完整文件路径。</summary>
        public string FilePath { get; }

        /// <summary>可读的失败原因，用于诊断，不含敏感内容。</summary>
        public string Reason { get; }
    }

    /// <summary>Activity 实时流端口。重连后调用方按 ActivityId 去重。</summary>
    public interface IActivityStream
    {
        IAsyncEnumerable<ActivityRecord> WatchAsync(ActivityWatchRequest request, CancellationToken cancellationToken);
    }
}
