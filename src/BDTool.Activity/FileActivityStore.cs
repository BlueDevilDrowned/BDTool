using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts.Activity;
using BDTool.Contracts.Artifacts;
using BDTool.Core.Projects;

namespace BDTool.Activity;

/// <summary>
/// Activity 的本地文件实现。每次调用写独立 JSON 文件，读取只枚举权威
/// Activity 根目录，查询、按 ID 读取与实时跟随共享同一套序列化约定。
/// </summary>
public sealed class FileActivityStore : IActivitySink, IActivityQuery, IActivityStream
{
    /// <summary>WatchAsync 未提供有效间隔时使用的默认轮询间隔。</summary>
    public const int DefaultPollIntervalMilliseconds = 1000;

    /// <summary>允许的最小轮询间隔，避免跟随模式忙等。</summary>
    public const int MinPollIntervalMilliseconds = 50;

    /// <summary>允许的最大轮询间隔。</summary>
    public const int MaxPollIntervalMilliseconds = 60_000;

    /// <summary>单次查询最多返回的摘要数量，避免手工构造的 Limit 造成无界内存占用。</summary>
    public const int MaxQueryLimit = 100_000;

    private static readonly JsonSerializerOptions s_jsonOptions = CreateJsonOptions();

    private readonly string _projectPath;

    /// <summary>使用项目根创建存储；所有路径都由 ProjectLayout 推导。</summary>
    public FileActivityStore(string projectPath)
    {
        _projectPath = ProjectLayout.ResolveProjectRoot(projectPath);
    }

    /// <summary>
    /// 写入或整文件替换一条 Activity。同一 ActivityId 只允许整体替换，
    /// 不允许追加；不同 ActivityId 写入不同文件，可以并发进行。
    /// </summary>
    public async Task RecordAsync(ActivityRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!ActivityPaths.IsValidActivityId(record.ActivityId, out var error))
        {
            throw new ArgumentException(error, nameof(record));
        }

        if (record.StartedAt == default)
        {
            throw new ArgumentException("StartedAt 必须是有效的开始时间。", nameof(record));
        }

        Normalize(record);

        var path = ActivityPaths.GetActivityFilePath(_projectPath, record.ActivityId, record.StartedAt);
        var payload = JsonSerializer.SerializeToUtf8Bytes(record, s_jsonOptions);
        await AtomicFile.WriteAsync(path, payload, overwrite: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>查询摘要；损坏文件被跳过，不影响其余记录。</summary>
    public async Task<IReadOnlyList<ActivitySummary>> QueryAsync(ActivityQuery query, CancellationToken cancellationToken)
    {
        var result = await QueryWithDiagnosticsAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Summaries;
    }

    /// <summary>
    /// 与 <see cref="QueryAsync"/> 相同，但额外返回无法解析的文件清单，
    /// 便于诊断数据损坏而不必把异常抛给调用方。
    /// </summary>
    public async Task<ActivityQueryResult> QueryWithDiagnosticsAsync(
        ActivityQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var records = new List<ActivityRecord>();
        var failures = new List<ActivityReadFailure>();

        foreach (var filePath in EnumerateActivityFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                records.Add(await ReadRecordAsync(filePath, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                failures.Add(new ActivityReadFailure(
                    filePath,
                    exception.GetType().Name + ": " + exception.Message));
            }
        }

        var summaries = records
            .Where(record => Matches(record, query))
            .OrderByDescending(record => record.StartedAt)
            .ThenByDescending(record => record.ActivityId, StringComparer.Ordinal)
            .Skip(Math.Max(0, query.Offset))
            .Take(ClampLimit(query.Limit))
            .Select(ToSummary)
            .ToArray();

        return new ActivityQueryResult(summaries, failures);
    }

    /// <summary>
    /// 按 ActivityId 读取完整记录。跨日期目录定位，找不到时抛出
    /// <see cref="FileNotFoundException"/>，不返回伪造的空记录。
    /// </summary>
    public async Task<ActivityRecord> GetAsync(string activityId, CancellationToken cancellationToken)
    {
        if (!ActivityPaths.IsValidActivityId(activityId, out var error))
        {
            throw new ArgumentException(error, nameof(activityId));
        }

        var fileName = activityId + ActivityPaths.FileExtension;
        var root = ProjectLayout.GetActivityRoot(_projectPath);
        foreach (var directory in GetDirectoriesOrEmpty(root)
                     .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            var candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate))
            {
                continue;
            }

            var record = await ReadRecordAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(record.ActivityId, activityId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Activity 文件 " + candidate + " 的 activityId 与文件名不一致。");
            }

            return record;
        }

        throw new FileNotFoundException("找不到 Activity 记录：" + activityId + "。", fileName);
    }

    /// <summary>
    /// 轮询式实时跟随。按 ActivityId 去重，Follow=false 时读完当前快照即结束。
    /// 轮询间隔被限制在 <see cref="MinPollIntervalMilliseconds"/> 与
    /// <see cref="MaxPollIntervalMilliseconds"/> 之间。
    /// </summary>
    public async IAsyncEnumerable<ActivityRecord> WatchAsync(
        ActivityWatchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var interval = ClampPollInterval(request.PollIntervalMilliseconds);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            var snapshot = await QueryAsync(
                new ActivityQuery { FromUtc = request.SinceUtc, Limit = MaxQueryLimit },
                cancellationToken).ConfigureAwait(false);

            // 查询按 StartedAt 倒序返回，跟随按时间正序推送更符合阅读顺序。
            foreach (var summary in snapshot.Reverse())
            {
                if (!seen.Add(summary.ActivityId))
                {
                    continue;
                }

                ActivityRecord record;
                try
                {
                    record = await GetAsync(summary.ActivityId, cancellationToken).ConfigureAwait(false);
                }
                catch (FileNotFoundException)
                {
                    // 查询与读取之间被清理掉的文件不构造替代记录。
                    continue;
                }

                yield return record;
            }

            if (!request.Follow)
            {
                yield break;
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static int ClampLimit(int limit)
    {
        if (limit <= 0)
        {
            return 0;
        }

        return Math.Min(limit, MaxQueryLimit);
    }

    private static int ClampPollInterval(int interval)
    {
        if (interval <= 0)
        {
            return DefaultPollIntervalMilliseconds;
        }

        return Math.Clamp(interval, MinPollIntervalMilliseconds, MaxPollIntervalMilliseconds);
    }

    /// <summary>只枚举 activity 根下的一层日期目录中的 *.json，不递归扫描。</summary>
    private IEnumerable<string> EnumerateActivityFiles()
    {
        var root = ProjectLayout.GetActivityRoot(_projectPath);
        foreach (var directory in GetDirectoriesOrEmpty(root))
        {
            foreach (var file in GetFilesOrEmpty(directory))
            {
                yield return file;
            }
        }
    }

    private static string[] GetDirectoriesOrEmpty(string root)
    {
        try
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>();
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return Array.Empty<string>();
        }
    }

    private static string[] GetFilesOrEmpty(string directory)
    {
        try
        {
            return Directory.GetFiles(directory, "*" + ActivityPaths.FileExtension, SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsReadFailure(Exception exception) =>
        exception is JsonException
            or IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or NotSupportedException;

    private static async Task<ActivityRecord> ReadRecordAsync(string filePath, CancellationToken cancellationToken)
    {
        byte[] bytes;
        await using (var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        ActivityRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<ActivityRecord>(bytes, s_jsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Activity 文件不是有效的 JSON：" + filePath, exception);
        }

        if (record is null)
        {
            throw new InvalidDataException("Activity 文件没有记录内容：" + filePath);
        }

        if (!ActivityPaths.IsValidActivityId(record.ActivityId, out var error))
        {
            throw new InvalidDataException("Activity 文件缺少有效 activityId（" + error + "）：" + filePath);
        }

        Normalize(record);
        return record;
    }

    /// <summary>把反序列化后可能为 null 的集合与嵌套对象修正为空实例，避免查询路径空引用。</summary>
    private static void Normalize(ActivityRecord record)
    {
        record.Actor ??= new ActivityActor();
        record.Actor.Type ??= string.Empty;
        record.Actor.AgentId ??= string.Empty;
        record.Actor.SessionId ??= string.Empty;
        record.Source ??= string.Empty;
        record.OperationId ??= string.Empty;
        record.ProviderId ??= string.Empty;
        record.JobId ??= string.Empty;
        record.Input ??= new ActivityPayload();
        record.Output ??= new ActivityPayload();
        record.Input.PreviewJson ??= "{}";
        record.Output.PreviewJson ??= "{}";
        record.Artifacts ??= new List<ArtifactReference>();
    }

    private static bool Matches(ActivityRecord record, ActivityQuery query)
    {
        if (query.FromUtc is { } from && record.StartedAt < from)
        {
            return false;
        }

        if (query.ToUtc is { } to && record.StartedAt > to)
        {
            return false;
        }

        if (!MatchesText(query.Source, record.Source)
            || !MatchesText(query.ActorType, record.Actor.Type)
            || !MatchesText(query.AgentId, record.Actor.AgentId)
            || !MatchesText(query.OperationId, record.OperationId)
            || !MatchesText(query.ProviderId, record.ProviderId))
        {
            return false;
        }

        if (query.Status is { } status && record.Status != status)
        {
            return false;
        }

        if (query.ErrorsOnly && record.Error is null)
        {
            return false;
        }

        if (query.HasArtifactsOnly && !HasArtifacts(record))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesText(string? filter, string value) =>
        string.IsNullOrWhiteSpace(filter)
        || string.Equals(filter, value, StringComparison.OrdinalIgnoreCase);

    private static bool HasArtifacts(ActivityRecord record) =>
        record.Artifacts.Count > 0
        || record.StdoutArtifact is not null
        || record.StderrArtifact is not null;

    private static ActivitySummary ToSummary(ActivityRecord record) =>
        new()
        {
            ActivityId = record.ActivityId,
            StartedAt = record.StartedAt,
            FinishedAt = record.FinishedAt,
            Source = record.Source,
            ActorType = record.Actor.Type,
            AgentId = record.Actor.AgentId,
            OperationId = record.OperationId,
            ProviderId = record.ProviderId,
            Status = record.Status,
            DurationMilliseconds = record.FinishedAt is { } finished
                ? (long)Math.Max(0, (finished - record.StartedAt).TotalMilliseconds)
                : null,
            JobId = record.JobId,
            ErrorCode = record.Error?.Code ?? string.Empty,
            HasArtifacts = HasArtifacts(record)
        };
}
