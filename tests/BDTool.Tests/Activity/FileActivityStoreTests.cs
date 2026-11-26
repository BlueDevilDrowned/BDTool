using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Activity;
using BDTool.Contracts;
using BDTool.Contracts.Activity;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Gateway;
using BDTool.Contracts.Operations;

namespace BDTool.Tests.Activity;

/// <summary>FileActivityStore 的落盘、查询、读取与跟随行为。</summary>
public sealed class FileActivityStoreTests
{
    [Fact]
    public async Task RecordAsync_WritesUtcDayFileWithCamelCaseAndStringEnum()
    {
        using var project = new TemporaryProject();
        var store = new FileActivityStore(project.ProjectPath);
        var record = CreateRecord(
            "act-1",
            new DateTimeOffset(2026, 11, 27, 2, 0, 0, TimeSpan.FromHours(8)),
            status: OperationStatus.Completed);

        await store.RecordAsync(record, CancellationToken.None);

        var path = project.Resolve(@"\.bdtool\state\activity\20261126\act-1.json");
        Assert.True(File.Exists(path));

        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"activityId\": \"act-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"completed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"previewJson\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Status\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordAsync_ReplacesWholeFileInsteadOfAppending()
    {
        using var project = new TemporaryProject();
        var store = new FileActivityStore(project.ProjectPath);
        var startedAt = DateTimeOffset.UtcNow;
        var first = CreateRecord("act-1", startedAt, status: OperationStatus.Running);
        var second = CreateRecord("act-1", startedAt, status: OperationStatus.Completed);

        await store.RecordAsync(first, CancellationToken.None);
        await store.RecordAsync(second, CancellationToken.None);

        var path = project.Resolve(@"\.bdtool\state\activity\" + startedAt.ToString("yyyyMMdd") + @"\act-1.json");
        var json = await File.ReadAllTextAsync(path);
        Assert.Equal(1, CountOccurrences(json, "\"activityId\""));

        var loaded = await store.GetAsync("act-1", CancellationToken.None);
        Assert.Equal(OperationStatus.Completed, loaded.Status);
    }

    [Fact]
    public async Task QueryAsync_FiltersSortsAndPages()
    {
        using var project = new TemporaryProject();
        var store = new FileActivityStore(project.ProjectPath);
        var day = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);

        await store.RecordAsync(CreateRecord("a", day.AddHours(10), ActivitySources.Cli, OperationStatus.Completed, "project.inspect"), CancellationToken.None);
        await store.RecordAsync(CreateRecord("b", day.AddHours(11), ActivitySources.Desktop, OperationStatus.Completed, "project.validate"), CancellationToken.None);
        await store.RecordAsync(CreateRecord("c", day.AddHours(12), ActivitySources.Cli, OperationStatus.Failed, "project.validate", errorCode: "EXECUTION_FAILED"), CancellationToken.None);
        await store.RecordAsync(CreateRecord("d", day.AddHours(9), ActivitySources.Cli, OperationStatus.Completed, "project.inspect"), CancellationToken.None);

        var all = await store.QueryAsync(new ActivityQuery(), CancellationToken.None);
        Assert.Equal(new[] { "c", "b", "a", "d" }, all.Select(item => item.ActivityId).ToArray());

        var cli = await store.QueryAsync(new ActivityQuery { Source = ActivitySources.Cli }, CancellationToken.None);
        Assert.Equal(new[] { "c", "a", "d" }, cli.Select(item => item.ActivityId).ToArray());

        var failed = await store.QueryAsync(new ActivityQuery { Status = OperationStatus.Failed }, CancellationToken.None);
        Assert.Equal(new[] { "c" }, failed.Select(item => item.ActivityId).ToArray());
        Assert.Equal("EXECUTION_FAILED", failed[0].ErrorCode);

        var errors = await store.QueryAsync(new ActivityQuery { ErrorsOnly = true }, CancellationToken.None);
        Assert.Equal(new[] { "c" }, errors.Select(item => item.ActivityId).ToArray());

        var byOperation = await store.QueryAsync(new ActivityQuery { OperationId = "project.validate" }, CancellationToken.None);
        Assert.Equal(new[] { "c", "b" }, byOperation.Select(item => item.ActivityId).ToArray());

        // 排序结果为 [c, b, a, d]；Offset 先跳过首条，再取 2 条。
        var page = await store.QueryAsync(new ActivityQuery { Limit = 2, Offset = 1 }, CancellationToken.None);
        Assert.Equal(new[] { "b", "a" }, page.Select(item => item.ActivityId).ToArray());

        var empty = await store.QueryAsync(new ActivityQuery { Limit = 0 }, CancellationToken.None);
        Assert.Empty(empty);

        Assert.All(all, summary => Assert.Equal(250, summary.DurationMilliseconds));
    }

    [Fact]
    public async Task QueryAsync_SkipsCorruptFileAndReportsIt()
    {
        using var project = new TemporaryProject();
        var store = new FileActivityStore(project.ProjectPath);
        var startedAt = new DateTimeOffset(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);
        await store.RecordAsync(CreateRecord("good", startedAt), CancellationToken.None);

        var dayDirectory = Path.Combine(project.ActivityRoot, "20261126");
        var corruptPath = Path.Combine(dayDirectory, "broken.json");
        await File.WriteAllTextAsync(corruptPath, "{ not json");

        var summaries = await store.QueryAsync(new ActivityQuery(), CancellationToken.None);
        Assert.Equal(new[] { "good" }, summaries.Select(item => item.ActivityId).ToArray());

        var diagnostics = await store.QueryWithDiagnosticsAsync(new ActivityQuery(), CancellationToken.None);
        var failure = Assert.Single(diagnostics.UnreadableFiles);
        Assert.Equal(corruptPath, failure.FilePath);
        Assert.Contains("InvalidDataException", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_EmptyStoreReturnsEmptyList()
    {
        using var project = new TemporaryProject();
        var store = new FileActivityStore(project.ProjectPath);

        var summaries = await store.QueryAsync(new ActivityQuery(), CancellationToken.None);

        Assert.Empty(summaries);
    }

    [Fact]
    public async Task GetAsync_MissingIdThrowsFileNotFound()
    {
        using var project = new TemporaryProject();
        var store = new FileActivityStore(project.ProjectPath);

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => store.GetAsync("missing-id", CancellationToken.None));

        Assert.Equal("missing-id.json", exception.FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    public async Task GetAsync_InvalidIdThrowsArgument(string activityId)
    {
        using var project = new TemporaryProject();
        var store = new FileActivityStore(project.ProjectPath);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.GetAsync(activityId, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_FindsRecordAcrossDayDirectories()
    {
        using var project = new TemporaryProject();
        var store = new FileActivityStore(project.ProjectPath);
        await store.RecordAsync(CreateRecord("old", new DateTimeOffset(2026, 11, 20, 1, 0, 0, TimeSpan.Zero)), CancellationToken.None);
        await store.RecordAsync(CreateRecord("new", new DateTimeOffset(2026, 11, 26, 1, 0, 0, TimeSpan.Zero)), CancellationToken.None);

        var loaded = await store.GetAsync("old", CancellationToken.None);

        Assert.Equal("old", loaded.ActivityId);
        Assert.Equal(OperationStatus.Completed, loaded.Status);
    }

    [Fact]
    public async Task RecordAsync_ConcurrentDistinctIdsDoNotOverwrite()
    {
        using var project = new TemporaryProject();
        var store = new FileActivityStore(project.ProjectPath);
        var day = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);

        await Task.WhenAll(Enumerable.Range(0, 24).Select(index =>
            store.RecordAsync(
                CreateRecord("act-" + index, day.AddMinutes(index)),
                CancellationToken.None)));

        var summaries = await store.QueryAsync(new ActivityQuery { Limit = 100 }, CancellationToken.None);
        Assert.Equal(24, summaries.Count);
        Assert.Equal(24, summaries.Select(item => item.ActivityId).Distinct(StringComparer.Ordinal).Count());

        foreach (var summary in summaries)
        {
            var record = await store.GetAsync(summary.ActivityId, CancellationToken.None);
            Assert.Equal(summary.ActivityId, record.ActivityId);
        }
    }

    [Fact]
    public async Task RecordAsync_FactoryInputRedactsSensitiveFields()
    {
        using var project = new TemporaryProject();
        var store = new FileActivityStore(project.ProjectPath);
        var context = new ActivityContext
        {
            ActivityId = "act-redact",
            TraceId = "trace-1",
            Source = ActivitySources.Cli
        };
        var request = new OperationRequest
        {
            OperationId = "project.inspect",
            InputJson = "{\"token\":\"super-secret-value\",\"nested\":{\"password\":\"p@ss\"},\"keep\":\"v\"}"
        };
        var record = ActivityRecordFactory.CreateStarted(
            context,
            request,
            new DateTimeOffset(2026, 11, 26, 8, 0, 0, TimeSpan.Zero));

        await store.RecordAsync(record, CancellationToken.None);

        var path = Path.Combine(project.ActivityRoot, "20261126", "act-redact.json");
        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("super-secret-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("p@ss", json, StringComparison.Ordinal);
        Assert.Contains("$redacted", json, StringComparison.Ordinal);

        var loaded = await store.GetAsync("act-redact", CancellationToken.None);
        Assert.True(loaded.Input.Redacted);
        Assert.Contains("\"keep\":\"v\"", loaded.Input.PreviewJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordAsync_LargeInputIsStoredAsSummaryNotInline()
    {
        using var project = new TemporaryProject();
        var store = new FileActivityStore(project.ProjectPath);
        var bigValue = new string('a', 70_000);
        var context = new ActivityContext { ActivityId = "act-big", TraceId = "trace-2" };
        var request = new OperationRequest
        {
            OperationId = "project.inspect",
            InputJson = "{\"data\":\"" + bigValue + "\"}"
        };
        var record = ActivityRecordFactory.CreateStarted(
            context,
            request,
            new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));

        await store.RecordAsync(record, CancellationToken.None);

        var path = Path.Combine(project.ActivityRoot, "20261126", "act-big.json");
        var info = new FileInfo(path);
        Assert.True(info.Exists);
        Assert.True(info.Length < 20_000, "超过 64 KiB 的输入不得内联进 Activity 文件。");

        var loaded = await store.GetAsync("act-big", CancellationToken.None);
        Assert.Contains("$artifact", loaded.Input.PreviewJson, StringComparison.Ordinal);
        Assert.DoesNotContain(bigValue, loaded.Input.PreviewJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WatchAsync_NonFollowReturnsSnapshotInAscendingOrderAndEnds()
    {
        using var project = new TemporaryProject();
        var store = new FileActivityStore(project.ProjectPath);
        var day = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
        await store.RecordAsync(CreateRecord("second", day.AddMinutes(2)), CancellationToken.None);
        await store.RecordAsync(CreateRecord("first", day.AddMinutes(1)), CancellationToken.None);

        var received = new List<string>();
        await foreach (var record in store.WatchAsync(new ActivityWatchRequest { Follow = false }, CancellationToken.None))
        {
            received.Add(record.ActivityId);
        }

        Assert.Equal(new[] { "first", "second" }, received);
    }

    [Fact]
    public async Task WatchAsync_FollowEmitsNewRecordsAndDeduplicates()
    {
        using var project = new TemporaryProject();
        var store = new FileActivityStore(project.ProjectPath);
        var day = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
        await store.RecordAsync(CreateRecord("r1", day.AddMinutes(1)), CancellationToken.None);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var received = new List<string>();
        var secondWritten = false;
        var thirdWritten = false;

        try
        {
            await foreach (var record in store.WatchAsync(
                new ActivityWatchRequest { PollIntervalMilliseconds = 50, Follow = true },
                cancellation.Token))
            {
                received.Add(record.ActivityId);

                if (!secondWritten)
                {
                    secondWritten = true;
                    await store.RecordAsync(CreateRecord("r2", day.AddMinutes(2)), CancellationToken.None);

                    // 重写同一个 ActivityId，用于验证跟随流不会重复推送。
                    await store.RecordAsync(
                        CreateRecord("r1", day.AddMinutes(1), status: OperationStatus.Partial),
                        CancellationToken.None);
                }
                else if (!thirdWritten)
                {
                    thirdWritten = true;
                    await store.RecordAsync(CreateRecord("r3", day.AddMinutes(3)), CancellationToken.None);
                }

                if (received.Count >= 3)
                {
                    cancellation.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 取消是跟随流的正常结束方式。
        }

        Assert.Equal(new[] { "r1", "r2", "r3" }, received);
    }

    private static ActivityRecord CreateRecord(
        string activityId,
        DateTimeOffset startedAt,
        string source = ActivitySources.Cli,
        OperationStatus status = OperationStatus.Completed,
        string operationId = "project.inspect",
        string providerId = "akio",
        string? errorCode = null)
    {
        return new ActivityRecord
        {
            ActivityId = activityId,
            TraceId = "trace-" + activityId,
            StartedAt = startedAt,
            FinishedAt = startedAt.AddMilliseconds(250),
            Source = source,
            Actor = new ActivityActor { Type = ActorTypes.Ai, AgentId = string.Empty, SessionId = "session-1" },
            OperationId = operationId,
            ProviderId = providerId,
            Status = status,
            Error = errorCode is null ? null : new KernelError { Code = errorCode, Message = "failed" }
        };
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
