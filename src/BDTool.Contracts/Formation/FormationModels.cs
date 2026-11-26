using System;
using System.Collections.Generic;

namespace BDTool.Contracts.Formation
{
    /// <summary>
    /// 编队持久化根。状态文件由唯一写入者原子替换；RequestReceipts 用于幂等重放。
    /// </summary>
    public sealed class FormationData
    {
        public int SchemaVersion { get; set; } = 1;
        public string Id { get; set; } = string.Empty;
        public string Objective { get; set; } = string.Empty;
        public string Status { get; set; } = FormationStatuses.InProgress;
        public int RequirementVersion { get; set; } = 1;
        public string CreationMode { get; set; } = DispatchModes.Manual;
        public string BlockedReason { get; set; } = string.Empty;
        public FormationBudgetData Budget { get; set; } = new FormationBudgetData();
        public List<FormationSeatData> Seats { get; set; } = new List<FormationSeatData>();
        public List<FormationTaskData> Tasks { get; set; } = new List<FormationTaskData>();
        public List<FormationArtifactData> Artifacts { get; set; } = new List<FormationArtifactData>();
        public Dictionary<string, FormationRequestReceiptData> RequestReceipts { get; set; } =
            new Dictionary<string, FormationRequestReceiptData>(StringComparer.Ordinal);
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>M7 默认预算。下级只能继承或收窄。</summary>
    public sealed class FormationBudgetData
    {
        public int SubagentMax { get; set; } = 2;
        public int SessionMax { get; set; }
        public int CrossAgentMax { get; set; }
        public int TreeSessionMax { get; set; } = 3;
        public int MaxDepth { get; set; } = 2;
        public int TimeoutMinutes { get; set; } = 60;
        public int ReviewRetryMax { get; set; } = 2;
    }

    /// <summary>编队席位。M7.1 的人工席位不会伪造 Agent/Session ID。</summary>
    public sealed class FormationSeatData
    {
        public string Id { get; set; } = string.Empty;
        public string Role { get; set; } = SeatRoles.Executor;
        public string DisplayName { get; set; } = string.Empty;
        public string DispatchMode { get; set; } = DispatchModes.Manual;
        public string AgentId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string AgentProfile { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Effort { get; set; } = string.Empty;
        public FormationWorkspaceData Workspace { get; set; } = new FormationWorkspaceData();
        public int SubagentMax { get; set; }
        public string ActiveTaskId { get; set; } = string.Empty;
    }

    /// <summary>项目内访问范围；WritePaths 统一保存为 POSIX 项目相对路径。</summary>
    public sealed class FormationWorkspaceData
    {
        public string Access { get; set; } = WorkspaceAccesses.Read;
        public List<string> WritePaths { get; set; } = new List<string>();
    }

    /// <summary>可人工推进的工作单元及其完整 Attempt/Submission/Review 历史。</summary>
    public sealed class FormationTaskData
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Objective { get; set; } = string.Empty;
        public string Instructions { get; set; } = string.Empty;
        public string Acceptance { get; set; } = string.Empty;
        public string ExecutorSeatId { get; set; } = string.Empty;
        public string ReviewerSeatId { get; set; } = string.Empty;
        public FormationWorkspaceData Workspace { get; set; } = new FormationWorkspaceData();
        public string State { get; set; } = FormationTaskStates.Ready;
        public int ReviewRetryMax { get; set; } = 2;
        public int ReviewFailureCount { get; set; }
        public List<FormationAttemptData> Attempts { get; set; } = new List<FormationAttemptData>();
        public List<FormationSubmissionData> Submissions { get; set; } =
            new List<FormationSubmissionData>();
        public List<FormationReviewData> Reviews { get; set; } = new List<FormationReviewData>();
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    /// <summary>一次派发请求。M7.1 最多进入 Dispatching，不伪造 Running。</summary>
    public sealed class FormationAttemptData
    {
        public string Id { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string Kind { get; set; } = "Execute";
        public string DispatchMode { get; set; } = DispatchModes.Manual;
        public string AgentId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string State { get; set; } = AttemptStates.Dispatching;
        public string ActorKind { get; set; } = FormationActorKinds.Human;
        public string ActorId { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string FailureCode { get; set; } = string.Empty;
    }

    /// <summary>执行者提交的结果摘要、变更范围和未验证项。</summary>
    public sealed class FormationSubmissionData
    {
        public string Id { get; set; } = string.Empty;
        public string AttemptId { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string ActorKind { get; set; } = FormationActorKinds.Human;
        public string ActorId { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public List<string> ChangedPaths { get; set; } = new List<string>();
        public List<FormationArtifactData> Artifacts { get; set; } = new List<FormationArtifactData>();
        public List<string> Unverified { get; set; } = new List<string>();
        public DateTimeOffset CreatedAt { get; set; }
    }

    /// <summary>Reviewer 的结构化审核，失败审核必须保留并要求新轮次返工。</summary>
    public sealed class FormationReviewData
    {
        public string Id { get; set; } = string.Empty;
        public string SubmissionId { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string ActorKind { get; set; } = FormationActorKinds.Human;
        public string ActorId { get; set; } = string.Empty;
        public string ReviewerSeatId { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public List<FormationReviewIssueData> Issues { get; set; } =
            new List<FormationReviewIssueData>();
        public List<string> MissingEvidence { get; set; } = new List<string>();
        public string Feedback { get; set; } = string.Empty;
        public string ReworkInstruction { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }

    public sealed class FormationReviewIssueData
    {
        public string Severity { get; set; } = ReviewSeverities.Error;
        public string Message { get; set; } = string.Empty;
        public string Evidence { get; set; } = string.Empty;
    }

    /// <summary>提交或编队级证据引用；不在这里读取文件内容。</summary>
    public sealed class FormationArtifactData
    {
        public string Kind { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
    }

    /// <summary>持久化幂等回执，防止同一 requestId 被重复执行。</summary>
    public sealed class FormationRequestReceiptData
    {
        public string RequestId { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;
        public string PayloadSha256 { get; set; } = string.Empty;
        public DateTimeOffset RecordedAt { get; set; }
    }

    /// <summary>一次成功 mutation 的结果；Replayed 表示 requestId 命中已有回执。</summary>
    public sealed class FormationCommandData
    {
        public string RequestId { get; set; } = string.Empty;
        public FormationData Formation { get; set; } = new FormationData();
        public bool Replayed { get; set; }
    }

    public sealed class FormationSummary
    {
        public string Id { get; set; } = string.Empty;
        public string Objective { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int RequirementVersion { get; set; }
        public int SeatCount { get; set; }
        public int TaskCount { get; set; }
        public int PassedTaskCount { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public sealed class FormationListData
    {
        public List<FormationSummary> Formations { get; set; } = new List<FormationSummary>();
    }
}
