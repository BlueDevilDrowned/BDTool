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
}
