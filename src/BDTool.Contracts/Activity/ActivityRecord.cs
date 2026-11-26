using System;
using System.Collections.Generic;
using BDTool.Contracts.Artifacts;
using BDTool.Contracts.Errors;

namespace BDTool.Contracts.Activity
{
    /// <summary>活动来源常量。</summary>
    public static class ActivitySources
    {
        public const string Cli = "cli";
        public const string Mcp = "mcp";
        public const string Desktop = "desktop";
        public const string Test = "test";
    }

    /// <summary>Actor 类型常量。</summary>
    public static class ActorTypes
    {
        public const string Ai = "ai";
        public const string Human = "human";
        public const string System = "system";
    }

    /// <summary>
    /// 调用者身份。没有真实 Agent ID 时 AgentId 必须为空，不得伪造。
    /// </summary>
    public sealed class ActivityActor
    {
        public string Type { get; set; } = ActorTypes.Ai;
        public string AgentId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
    }

    /// <summary>
    /// 输入或输出的审计载荷。PreviewJson 是已脱敏、已截断的摘要；
    /// 大内容必须在 Artifact 中保存，不能内联到 Activity 文件。
    /// </summary>
    public sealed class ActivityPayload
    {
        public bool Redacted { get; set; } = true;
        public string PreviewJson { get; set; } = "{}";
        public ArtifactReference? Artifact { get; set; }
    }

    /// <summary>
    /// 一次 Application 调用的完整审计记录。
    /// 每个活动写入独立文件：&lt;project&gt;/.bdtool/state/activity/&lt;yyyyMMdd&gt;/&lt;activityId&gt;.json。
    /// </summary>
    public sealed class ActivityRecord
    {
        public string ActivityId { get; set; } = string.Empty;
        public string TraceId { get; set; } = string.Empty;
        public string? ParentActivityId { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string Source { get; set; } = ActivitySources.Cli;
        public ActivityActor Actor { get; set; } = new ActivityActor();
        public string OperationId { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public OperationStatus Status { get; set; } = OperationStatus.Queued;
        public ActivityPayload Input { get; set; } = new ActivityPayload();
        public ActivityPayload Output { get; set; } = new ActivityPayload();
        public ArtifactReference? StdoutArtifact { get; set; }
        public ArtifactReference? StderrArtifact { get; set; }
        public int? ExitCode { get; set; }
        public KernelError? Error { get; set; }
        public string JobId { get; set; } = string.Empty;
        public List<string> ChangedAssets { get; set; } = new List<string>();
        public List<ArtifactReference> Artifacts { get; set; } = new List<ArtifactReference>();
    }

    /// <summary>Activity 列表的轻量摘要，不包含大内容。</summary>
    public sealed class ActivitySummary
    {
        public string ActivityId { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string Source { get; set; } = string.Empty;
        public string ActorType { get; set; } = string.Empty;
        public string AgentId { get; set; } = string.Empty;
        public string OperationId { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public OperationStatus Status { get; set; }
        public long? DurationMilliseconds { get; set; }
        public string JobId { get; set; } = string.Empty;
        public string ErrorCode { get; set; } = string.Empty;
        public bool HasArtifacts { get; set; }
    }

    /// <summary>Activity 查询条件。时间范围统一使用 UTC。</summary>
    public sealed class ActivityQuery
    {
        public DateTimeOffset? FromUtc { get; set; }
        public DateTimeOffset? ToUtc { get; set; }
        public string? Source { get; set; }
        public string? ActorType { get; set; }
        public string? AgentId { get; set; }
        public string? OperationId { get; set; }
        public string? ProviderId { get; set; }
        public OperationStatus? Status { get; set; }
        public bool ErrorsOnly { get; set; }
        public bool HasArtifactsOnly { get; set; }
        public int Limit { get; set; } = 100;
        public int Offset { get; set; }
    }

    /// <summary>实时跟随请求。Follow=false 时读取当前快照后结束。</summary>
    public sealed class ActivityWatchRequest
    {
        public DateTimeOffset? SinceUtc { get; set; }
        public int PollIntervalMilliseconds { get; set; } = 1000;
        public bool Follow { get; set; } = true;
    }
}
