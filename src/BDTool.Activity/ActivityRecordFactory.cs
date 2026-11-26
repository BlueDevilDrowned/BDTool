using System;
using System.Linq;
using BDTool.Contracts;
using BDTool.Contracts.Activity;
using BDTool.Contracts.Gateway;
using BDTool.Contracts.Operations;

namespace BDTool.Activity;

/// <summary>
/// 把一次 Application 调用映射为 ActivityRecord。工厂只负责数据映射，
/// 不负责落盘；持久化由后续 IActivitySink 实现完成。
/// </summary>
public static class ActivityRecordFactory
{
    /// <summary>创建已开始但尚未完成的记录。</summary>
    public static ActivityRecord CreateStarted(
        ActivityContext context,
        OperationRequest request,
        DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var input = ActivityContentPolicy.Summarize(request.InputJson, "input");
        return new ActivityRecord
        {
            ActivityId = context.ActivityId,
            TraceId = context.TraceId,
            ParentActivityId = context.ParentActivityId,
            StartedAt = startedAt,
            Source = context.Source,
            Actor = new ActivityActor
            {
                Type = context.Actor.Type,
                AgentId = context.Actor.AgentId,
                SessionId = context.Actor.SessionId
            },
            OperationId = request.OperationId,
            Status = OperationStatus.Running,
            Input = new ActivityPayload
            {
                Redacted = input.Redacted,
                PreviewJson = input.PreviewJson
            }
        };
    }

    /// <summary>
    /// 用操作结果补全记录。ExitCode 由 Application 按统一规则计算后传入，
    /// 避免 Activity 程序集反向依赖 Application。
    /// </summary>
    public static void Complete(
        ActivityRecord record,
        OperationResult result,
        int exitCode,
        DateTimeOffset finishedAt)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(result);

        var output = ActivityContentPolicy.Summarize(SerializeForPreview(result), "output");
        record.FinishedAt = finishedAt;
        record.Status = result.Status;
        record.ExitCode = exitCode;
        record.Error = result.Errors.FirstOrDefault();
        record.Output = new ActivityPayload
        {
            Redacted = output.Redacted,
            PreviewJson = output.PreviewJson
        };
        record.Artifacts.AddRange(result.Artifacts);
    }

    private static string SerializeForPreview(OperationResult result)
    {
        return System.Text.Json.JsonSerializer.Serialize(
            result,
            result.GetType(),
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters =
                {
                    new System.Text.Json.Serialization.JsonStringEnumConverter(
                        System.Text.Json.JsonNamingPolicy.CamelCase)
                }
            });
    }
}
