using System.Collections.Generic;

namespace BDTool.Contracts.Formation
{
    /// <summary>编队生命周期状态。</summary>
    public static class FormationStatuses
    {
        public const string InProgress = "InProgress";
        public const string CancelRequested = "CancelRequested";
        public const string Blocked = "Blocked";
        public const string Completed = "Completed";
        public const string Cancelled = "Cancelled";
        public const string Archived = "Archived";

        public static readonly IReadOnlyList<string> All = new[]
        {
            InProgress, CancelRequested, Blocked, Completed, Cancelled, Archived
        };
    }

    /// <summary>M7 首批席位角色；其他角色留给后续阶段。</summary>
    public static class SeatRoles
    {
        public const string Coordinator = "Coordinator";
        public const string Executor = "Executor";
        public const string Reviewer = "Reviewer";

        public static readonly IReadOnlyList<string> All = new[]
        {
            Coordinator, Executor, Reviewer
        };
    }

    /// <summary>席位的项目访问模式。</summary>
    public static class WorkspaceAccesses
    {
        public const string Read = "read";
        public const string Write = "write";

        public static readonly IReadOnlyList<string> All = new[] { Read, Write };
    }

    /// <summary>任务状态机词表。Running 只允许由 M7.2 绑定真实 Agent/Session 后使用。</summary>
    public static class FormationTaskStates
    {
        public const string Ready = "Ready";
        public const string DispatchRequested = "DispatchRequested";
        public const string ManualInProgress = "ManualInProgress";
        public const string Running = "Running";
        public const string Submitted = "Submitted";
        public const string Reviewing = "Reviewing";
        public const string NeedsRework = "NeedsRework";
        public const string Passed = "Passed";
        public const string Blocked = "Blocked";
        public const string Unsupported = "Unsupported";
        public const string CancelRequested = "CancelRequested";
        public const string CancelUnconfirmed = "CancelUnconfirmed";
        public const string Cancelled = "Cancelled";
        public const string DeliveryUnknown = "DeliveryUnknown";
    }

    /// <summary>一次派发尝试的状态。</summary>
    public static class AttemptStates
    {
        public const string Dispatching = "Dispatching";
        public const string ManualInProgress = "ManualInProgress";
        public const string Running = "Running";
        public const string Submitted = "Submitted";
        public const string Unsupported = "Unsupported";
        public const string DeliveryUnknown = "DeliveryUnknown";
        public const string CancelRequested = "CancelRequested";
        public const string Cancelled = "Cancelled";
        public const string Failed = "Failed";
    }

    /// <summary>派发模式；原生与 Session 必须由宿主真实身份绑定后才能进入 Running。</summary>
    public static class DispatchModes
    {
        public const string Manual = "Manual";
        public const string Native = "Native";
        public const string Session = "Session";
        public const string Coordinator = "Coordinator";
    }

    /// <summary>审核问题严重度。</summary>
    public static class ReviewSeverities
    {
        public const string Error = "error";
        public const string Warning = "warning";
        public const string Information = "info";
    }

    /// <summary>操作来源。未绑定真实运行时的命令只能写 Human。</summary>
    public static class FormationActorKinds
    {
        public const string Human = "Human";
        public const string Agent = "Agent";
        public const string Coordinator = "Coordinator";
        public const string System = "System";
    }

    public static class FormationRequestCommands
    {
        public const string Create = "formation.create";
        public const string AddSeat = "formation.seat.add";
        public const string CreateTask = "formation.task.create";
        public const string DispatchTask = "formation.task.dispatch";
        public const string StartTask = "formation.task.start";
        public const string SubmitTask = "formation.task.submit";
        public const string ReviewTask = "formation.task.review";
        public const string PrepareRework = "formation.task.rework";
        public const string PrepareNativeDispatch = "formation.task.native-prepare";
        public const string PrepareSessionDispatch = "formation.task.session-prepare";
        public const string BindRuntimeIdentity = "formation.task.bind-runtime";
        public const string CollectResult = "formation.task.collect";
        public const string MarkUnsupported = "formation.task.unsupported";
        public const string MarkDeliveryUnknown = "formation.task.delivery-unknown";
        public const string CancelTask = "formation.task.cancel";
        public const string ConfirmTaskCancellation = "formation.task.cancel-confirm";
        public const string CancelFormation = "formation.cancel";
        public const string ArchiveFormation = "formation.archive";
    }
}
