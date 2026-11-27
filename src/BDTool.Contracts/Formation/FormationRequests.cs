using System.Collections.Generic;

namespace BDTool.Contracts.Formation
{
    public sealed class CreateFormationRequest
    {
        public string FormationId { get; set; } = string.Empty;
        public string Objective { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public FormationBudgetData? Budget { get; set; }
    }

    public sealed class AddSeatRequest
    {
        public string FormationId { get; set; } = string.Empty;
        public string SeatId { get; set; } = string.Empty;
        public string Role { get; set; } = SeatRoles.Executor;
        public string DisplayName { get; set; } = string.Empty;
        public string AgentProfile { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Effort { get; set; } = string.Empty;
        public string DispatchMode { get; set; } = DispatchModes.Manual;
        public string Access { get; set; } = WorkspaceAccesses.Read;
        public List<string> WritePaths { get; set; } = new List<string>();
        public int SubagentMax { get; set; }
        public string RequestId { get; set; } = string.Empty;
    }

    public sealed class CreateFormationTaskRequest
    {
        public string FormationId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Objective { get; set; } = string.Empty;
        public string Instructions { get; set; } = string.Empty;
        public string Acceptance { get; set; } = string.Empty;
        public string ExecutorSeatId { get; set; } = string.Empty;
        public string ReviewerSeatId { get; set; } = string.Empty;
        public string Access { get; set; } = WorkspaceAccesses.Read;
        public List<string> WritePaths { get; set; } = new List<string>();
        public int ReviewRetryMax { get; set; } = 2;
        public string RequestId { get; set; } = string.Empty;
    }

    public sealed class TaskCommandRequest
    {
        public string FormationId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string ActorId { get; set; } = "manual-user";
    }

    public sealed class SubmitFormationTaskRequest
    {
        public string FormationId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string ActorId { get; set; } = "manual-user";
        public string Summary { get; set; } = string.Empty;
        public List<string> ChangedPaths { get; set; } = new List<string>();
        public List<FormationArtifactData> Artifacts { get; set; } =
            new List<FormationArtifactData>();
        public List<string> Unverified { get; set; } = new List<string>();
    }

    public sealed class ReviewFormationTaskRequest
    {
        public string FormationId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string ReviewerSeatId { get; set; } = string.Empty;
        public string ActorId { get; set; } = "manual-user";
        public string SubmissionId { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public List<FormationReviewIssueData> Issues { get; set; } =
            new List<FormationReviewIssueData>();
        public List<string> MissingEvidence { get; set; } = new List<string>();
        public string Feedback { get; set; } = string.Empty;
        public string ReworkInstruction { get; set; } = string.Empty;
    }

    /// <summary>
    /// 生成原生子智能体或独立 Session 的派发包。Session 模式必须显式携带用户授权；
    /// 两种模式都要求对应 Executor 席位已声明相同 DispatchMode。
    /// </summary>
    public sealed class PrepareTaskDispatchRequest
    {
        public string FormationId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string ActorId { get; set; } = "coordinator";
        public string DispatchMode { get; set; } = DispatchModes.Native;
        public bool UserAuthorized { get; set; }
        public string AuthorizationQuote { get; set; } = string.Empty;
        public string AuthorizationScope { get; set; } = string.Empty;
        public string AgentProfile { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Effort { get; set; } = string.Empty;
    }

    /// <summary>
    /// 回填宿主真实返回的 AgentId/SessionId。BindingEvidence 应记录宿主工具或回读来源，
    /// 用于区分真实绑定与调用方伪造的普通字符串。
    /// </summary>
    public sealed class BindTaskRuntimeRequest
    {
        public string FormationId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string ActorId { get; set; } = "coordinator";
        public string AgentId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string BindingEvidence { get; set; } = string.Empty;
        public FormationHostAttestationData HostAttestation { get; set; } = new();
    }

    /// <summary>记录宿主不支持和结果未知；两者都必须保留失败事实且禁止自动重派。</summary>
    public sealed class ReportTaskRuntimeFailureRequest
    {
        public string FormationId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string ActorId { get; set; } = "coordinator";
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Coordinator 回收宿主结果。Running 任务只能经该入口提交，且必须带来源引用；
    /// 本地人工结果继续走 SubmitFormationTaskRequest。
    /// </summary>
    public sealed class CollectFormationTaskResultRequest
    {
        public string FormationId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string ActorId { get; set; } = "coordinator";
        public string Summary { get; set; } = string.Empty;
        public List<string> ChangedPaths { get; set; } = new List<string>();
        public List<FormationArtifactData> Artifacts { get; set; } =
            new List<FormationArtifactData>();
        public List<string> Unverified { get; set; } = new List<string>();
        public FormationHostResultReferenceData HostResult { get; set; } = new();
    }

    /// <summary>Running 任务收到宿主取消后，由 Coordinator 单独确认本地终止状态。</summary>
    public sealed class ConfirmTaskCancellationRequest
    {
        public string FormationId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public string ActorId { get; set; } = "coordinator";
        public string CancellationEvidence { get; set; } = string.Empty;
    }
}
