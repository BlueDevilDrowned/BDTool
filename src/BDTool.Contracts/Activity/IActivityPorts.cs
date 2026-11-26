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

    /// <summary>Activity 实时流端口。重连后调用方按 ActivityId 去重。</summary>
    public interface IActivityStream
    {
        IAsyncEnumerable<ActivityRecord> WatchAsync(ActivityWatchRequest request, CancellationToken cancellationToken);
    }
}
