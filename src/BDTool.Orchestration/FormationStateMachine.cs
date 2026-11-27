using BDTool.Contracts.Errors;
using BDTool.Contracts.Formation;
using BDTool.Core.Workspace;

namespace BDTool.Orchestration;

/// <summary>
/// Formation 纯领域状态机。这里不执行 IO，只验证状态前置条件并更新内存对象，
/// 让文件锁和原子提交由外层服务统一负责。
/// </summary>
internal static class FormationStateMachine
{
    private const string ReviewRetryExceeded = "REVIEW_RETRY_EXCEEDED";
    private const string FormationCancelled = "FORMATION_CANCELLED";
    private const string TaskCancelled = "MANUAL_CANCELLED";

    public static void EnsureStructureMutable(FormationData formation)
    {
        EnsureFormationStatus(
            formation,
            FormationStatuses.InProgress,
            "Formation 当前状态不允许新增或修改结构。");
    }

    public static FormationTaskData RequireTask(FormationData formation, string taskId) =>
        formation.Tasks.FirstOrDefault(task =>
            string.Equals(task.Id, taskId, StringComparison.Ordinal))
        ?? throw NotFound("Task", taskId);

    public static FormationSeatData RequireSeat(
        FormationData formation,
        string seatId,
        string role)
    {
        var seat = formation.Seats.FirstOrDefault(item =>
            string.Equals(item.Id, seatId, StringComparison.Ordinal))
            ?? throw NotFound("Seat", seatId);
        if (!string.Equals(seat.Role, role, StringComparison.Ordinal))
        {
            throw Conflict("Seat 角色不符合任务要求。", seatId, "seatId");
        }

        return seat;
    }

    /// <summary>
    /// M7.2 进入 Running 的唯一守卫。只有宿主返回的真实 AgentId 或 SessionId
    /// 至少一个非空时才允许通过，禁止用随机值或请求受理状态代替。
    /// </summary>
    public static void EnsureCanEnterRunning(string? agentId, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(agentId) && string.IsNullOrWhiteSpace(sessionId))
        {
            throw Conflict(
                "没有真实 AgentId 或 SessionId，不能进入 Running。",
                "missing-runtime-identity",
                "agentId/sessionId");
        }
    }

    public static void Dispatch(
        FormationData formation,
        string taskId,
        string requestId,
        string actorId)
    {
        EnsureFormationStatus(formation, FormationStatuses.InProgress, "Formation 当前状态不允许派发。");
        var task = RequireTask(formation, taskId);
        if (!string.Equals(task.State, FormationTaskStates.Ready, StringComparison.Ordinal))
        {
            throw Conflict("只有 Ready 任务可以请求派发。", task.State, "state");
        }

        var seat = RequireSeat(formation, task.ExecutorSeatId, SeatRoles.Executor);
        if (!string.IsNullOrWhiteSpace(seat.ActiveTaskId)
            && !string.Equals(seat.ActiveTaskId, task.Id, StringComparison.Ordinal))
        {
            throw Conflict("Executor 席位已有其他活动任务。", seat.ActiveTaskId, "activeTaskId");
        }

        var now = DateTimeOffset.UtcNow;
        seat.ActiveTaskId = task.Id;
        task.Attempts.Add(new FormationAttemptData
        {
            Id = FormationRepository.NextId("attempt"),
            RequestId = requestId,
            DispatchMode = DispatchModes.Manual,
            State = AttemptStates.Dispatching,
            ActorKind = FormationActorKinds.Human,
            ActorId = actorId,
            StartedAt = now
        });
        task.State = FormationTaskStates.DispatchRequested;
        task.UpdatedAt = now;
    }

    public static void StartManual(FormationData formation, string taskId)
    {
        EnsureFormationStatus(formation, FormationStatuses.InProgress, "Formation 当前状态不允许开始任务。");
        var task = RequireTask(formation, taskId);
        if (!string.Equals(task.State, FormationTaskStates.DispatchRequested, StringComparison.Ordinal))
        {
            throw Conflict("只有 DispatchRequested 任务可以进入人工执行。", task.State, "state");
        }

        var attempt = RequireActiveAttempt(task, AttemptStates.Dispatching);
        attempt.State = AttemptStates.ManualInProgress;
        task.State = FormationTaskStates.ManualInProgress;
        task.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static FormationAttemptData PrepareRuntimeDispatch(
        FormationData formation,
        string taskId,
        string dispatchMode,
        string requestId,
        string actorId,
        string sessionAuthorizationId,
        int effectiveMaxDepth)
    {
        EnsureFormationStatus(
            formation,
            FormationStatuses.InProgress,
            "Formation 当前状态不允许生成派发包。");
        var task = RequireTask(formation, taskId);
        if (!string.Equals(task.State, FormationTaskStates.Ready, StringComparison.Ordinal))
        {
            throw Conflict("只有 Ready 任务可以生成派发包。", task.State, "state");
        }

        var seat = RequireSeat(formation, task.ExecutorSeatId, SeatRoles.Executor);
        if (!string.Equals(seat.DispatchMode, dispatchMode, StringComparison.Ordinal))
        {
            throw Conflict(
                "Executor 席位 DispatchMode 与派发模式不一致。",
                seat.DispatchMode,
                "dispatchMode");
        }

        if (!string.IsNullOrWhiteSpace(seat.ActiveTaskId)
            && !string.Equals(seat.ActiveTaskId, task.Id, StringComparison.Ordinal))
        {
            throw Conflict("Executor 席位已有其他活动任务。", seat.ActiveTaskId, "activeTaskId");
        }

        var now = DateTimeOffset.UtcNow;
        seat.ActiveTaskId = task.Id;
        seat.AgentId = string.Empty;
        seat.SessionId = string.Empty;
        var attempt = new FormationAttemptData
        {
            Id = FormationRepository.NextId("attempt"),
            RequestId = requestId,
            DispatchMode = dispatchMode,
            State = AttemptStates.Dispatching,
            ActorKind = FormationActorKinds.Human,
            ActorId = actorId,
            SessionAuthorizationId = sessionAuthorizationId,
            EffectiveSubagentMax = 0,
            EffectiveMaxDepth = effectiveMaxDepth,
            StartedAt = now
        };
        task.Attempts.Add(attempt);
        task.State = FormationTaskStates.DispatchRequested;
        task.UpdatedAt = now;
        return attempt;
    }

    public static void BindRuntime(
        FormationData formation,
        string taskId,
        string agentId,
        string sessionId,
        FormationHostAttestationData attestation)
    {
        EnsureFormationStatus(
            formation,
            FormationStatuses.InProgress,
            "Formation 当前状态不允许绑定运行时身份。");
        var task = RequireTask(formation, taskId);
        if (!string.Equals(task.State, FormationTaskStates.DispatchRequested, StringComparison.Ordinal))
        {
            throw Conflict("只有 DispatchRequested 任务可以绑定运行时身份。", task.State, "state");
        }

        var attempt = RequireActiveAttempt(task, AttemptStates.Dispatching);
        if (string.Equals(attempt.DispatchMode, DispatchModes.Native, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(agentId))
        {
            throw Conflict("Native 模式必须绑定真实 AgentId。", "missing-agent-id", "agentId");
        }

        if (string.Equals(attempt.DispatchMode, DispatchModes.Session, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(sessionId))
        {
            throw Conflict("Session 模式必须绑定真实 SessionId。", "missing-session-id", "sessionId");
        }

        if (string.Equals(attempt.DispatchMode, DispatchModes.Session, StringComparison.Ordinal))
        {
            var authorization = formation.SessionAuthorizations.FirstOrDefault(item =>
                string.Equals(
                    item.Id,
                    attempt.SessionAuthorizationId,
                    StringComparison.Ordinal));
            if (authorization is null)
            {
                throw Conflict(
                    "Session Attempt 缺少持久化的用户授权记录。",
                    attempt.Id,
                    "sessionAuthorizationId");
            }

            if (authorization.ExpiresAt is not null
                && authorization.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw Conflict(
                    "Session 用户授权已过期。",
                    authorization.Id,
                    "sessionAuthorizationId");
            }

            if (!string.IsNullOrWhiteSpace(authorization.Scope)
                && !string.Equals(authorization.Scope, task.Id, StringComparison.Ordinal)
                && !string.Equals(authorization.Scope, "*", StringComparison.Ordinal))
            {
                throw Conflict(
                    "Session 用户授权范围不覆盖当前任务。",
                    authorization.Scope,
                    "authorizationScope");
            }
        }

        var duplicateIdentity = formation.Tasks
            .SelectMany(item => item.Attempts)
            .Any(item =>
                !string.Equals(item.Id, attempt.Id, StringComparison.Ordinal)
                && ((!string.IsNullOrWhiteSpace(agentId)
                        && string.Equals(item.AgentId, agentId.Trim(), StringComparison.Ordinal))
                    || (!string.IsNullOrWhiteSpace(sessionId)
                        && string.Equals(item.SessionId, sessionId.Trim(), StringComparison.Ordinal))));
        if (duplicateIdentity)
        {
            throw Conflict(
                "宿主身份已被本 Formation 的其他 Attempt 使用。",
                string.IsNullOrWhiteSpace(agentId) ? sessionId : agentId,
                string.IsNullOrWhiteSpace(agentId) ? "sessionId" : "agentId");
        }

        EnsureCanEnterRunning(agentId, sessionId);
        var seat = RequireSeat(formation, task.ExecutorSeatId, SeatRoles.Executor);
        var now = DateTimeOffset.UtcNow;
        attempt.AgentId = agentId.Trim();
        attempt.SessionId = sessionId.Trim();
        attempt.BindingEvidence = attestation.Evidence;
        attempt.BindingAttestation = new FormationHostAttestationData
        {
            HostId = attestation.HostId,
            ToolCallId = attestation.ToolCallId,
            CapturedAt = attestation.CapturedAt,
            RawResultSha256 = attestation.RawResultSha256,
            Evidence = attestation.Evidence
        };
        attempt.BoundAt = now;
        attempt.State = AttemptStates.Running;
        seat.AgentId = attempt.AgentId;
        seat.SessionId = attempt.SessionId;
        task.State = FormationTaskStates.Running;
        task.UpdatedAt = now;
    }

    public static void Submit(
        FormationData formation,
        string taskId,
        FormationSubmissionData submission)
    {
        EnsureFormationStatus(formation, FormationStatuses.InProgress, "Formation 当前状态不允许提交。");
        var task = RequireTask(formation, taskId);
        if (!string.Equals(
            task.State,
            FormationTaskStates.ManualInProgress,
            StringComparison.Ordinal))
        {
            throw Conflict(
                "人工 submit 只接受 ManualInProgress；Running 结果必须使用 collect。",
                task.State,
                "state");
        }

        var attempt = RequireActiveAttempt(task, AttemptStates.ManualInProgress);
        CompleteSubmission(formation, task, attempt, submission);
    }

    public static void CollectRuntimeResult(
        FormationData formation,
        string taskId,
        FormationSubmissionData submission)
    {
        EnsureFormationStatus(
            formation,
            FormationStatuses.InProgress,
            "Formation 当前状态不允许回收结果。");
        var task = RequireTask(formation, taskId);
        if (!string.Equals(task.State, FormationTaskStates.Running, StringComparison.Ordinal))
        {
            throw Conflict("只有 Running 任务可以回收宿主结果。", task.State, "state");
        }

        var attempt = RequireActiveAttempt(task, AttemptStates.Running);
        var boundIdentity = string.IsNullOrWhiteSpace(attempt.AgentId)
            ? attempt.SessionId
            : attempt.AgentId;
        if (string.IsNullOrWhiteSpace(submission.AttributedAgentId)
            || !string.Equals(
                submission.AttributedAgentId,
                boundIdentity,
                StringComparison.Ordinal))
        {
            throw Conflict(
                "Result 归属身份与绑定的 AgentId/SessionId 不一致。",
                submission.AttributedAgentId,
                "attributedAgentId");
        }

        if (submission.HostResult is null)
        {
            throw Conflict(
                "Running 结果必须包含宿主来源引用。",
                "missing-host-result",
                "hostResult");
        }

        CompleteSubmission(formation, task, attempt, submission);
    }

    private static void CompleteSubmission(
        FormationData formation,
        FormationTaskData task,
        FormationAttemptData attempt,
        FormationSubmissionData submission)
    {
        var now = submission.CreatedAt;
        attempt.State = AttemptStates.Submitted;
        attempt.CompletedAt = now;
        task.Submissions.Add(submission);
        foreach (var artifact in submission.Artifacts)
        {
            if (!formation.Artifacts.Any(existing =>
                    string.Equals(existing.Path, artifact.Path, PathPolicy.PathComparison)
                    && string.Equals(
                        existing.Sha256,
                        artifact.Sha256,
                        StringComparison.OrdinalIgnoreCase)))
            {
                formation.Artifacts.Add(new FormationArtifactData
                {
                    Kind = artifact.Kind,
                    Path = artifact.Path,
                    Sha256 = artifact.Sha256
                });
            }
        }

        task.State = FormationTaskStates.Submitted;
        task.UpdatedAt = now;
    }

    public static void Review(
        FormationData formation,
        FormationTaskData task,
        FormationReviewData review)
    {
        EnsureFormationStatus(formation, FormationStatuses.InProgress, "Formation 当前状态不允许审核。");
        if (!string.Equals(task.State, FormationTaskStates.Submitted, StringComparison.Ordinal))
        {
            throw Conflict("只有 Submitted 任务可以审核。", task.State, "state");
        }

        var latestSubmission = task.Submissions.LastOrDefault()
            ?? throw NotFound("Submission", review.SubmissionId);
        if (!string.Equals(latestSubmission.Id, review.SubmissionId, StringComparison.Ordinal))
        {
            throw Conflict(
                "只能审核最新一次提交。",
                review.SubmissionId,
                "submissionId");
        }

        if (task.Reviews.Any(item =>
                string.Equals(item.SubmissionId, review.SubmissionId, StringComparison.Ordinal)))
        {
            throw Conflict("该提交已经有审核记录。", review.SubmissionId, "submissionId");
        }

        if (review.Passed
            && review.Issues.Any(issue =>
                string.Equals(issue.Severity, ReviewSeverities.Error, StringComparison.Ordinal)))
        {
            throw new FormationServiceException(
                ErrorCodes.InvalidArgument,
                "Passed review 不能包含 error 级 issue。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["field"] = "issues"
                });
        }

        if (!review.Passed && string.IsNullOrWhiteSpace(review.ReworkInstruction))
        {
            throw new FormationServiceException(
                ErrorCodes.InvalidArgument,
                "失败审核必须提供 reworkInstruction。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["field"] = "reworkInstruction"
                });
        }

        task.Reviews.Add(review);
        var now = review.CreatedAt;
        if (review.Passed)
        {
            task.State = FormationTaskStates.Passed;
            ClearActiveSeat(formation, task.ExecutorSeatId);
            if (formation.Tasks.Count > 0
                && formation.Tasks.All(item =>
                    string.Equals(item.State, FormationTaskStates.Passed, StringComparison.Ordinal)))
            {
                formation.Status = FormationStatuses.Completed;
                formation.BlockedReason = string.Empty;
            }
        }
        else
        {
            task.ReviewFailureCount++;
            if (task.ReviewFailureCount > task.ReviewRetryMax)
            {
                task.State = FormationTaskStates.Blocked;
                formation.Status = FormationStatuses.Blocked;
                formation.BlockedReason = ReviewRetryExceeded;
                ClearActiveSeat(formation, task.ExecutorSeatId);
            }
            else
            {
                task.State = FormationTaskStates.NeedsRework;
            }
        }

        task.UpdatedAt = now;
    }

    public static void PrepareRework(FormationData formation, string taskId)
    {
        EnsureFormationStatus(formation, FormationStatuses.InProgress, "Formation 当前状态不允许返工。");
        var task = RequireTask(formation, taskId);
        if (!string.Equals(task.State, FormationTaskStates.NeedsRework, StringComparison.Ordinal))
        {
            throw Conflict("只有 NeedsRework 任务可以准备下一轮。", task.State, "state");
        }

        task.State = FormationTaskStates.Ready;
        task.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static void MarkUnsupported(
        FormationData formation,
        string taskId,
        string reason)
    {
        var task = RequireRuntimeTask(formation, taskId);
        var attempt = RequireActiveAttempt(task, AttemptStates.Dispatching);
        var now = DateTimeOffset.UtcNow;
        attempt.State = AttemptStates.Unsupported;
        attempt.CompletedAt = now;
        attempt.FailureCode = string.IsNullOrWhiteSpace(reason)
            ? "CAPABILITY_UNSUPPORTED"
            : "CAPABILITY_UNSUPPORTED: " + reason;
        task.State = FormationTaskStates.Unsupported;
        task.UpdatedAt = now;
        formation.Status = FormationStatuses.Blocked;
        formation.BlockedReason = "CAPABILITY_UNSUPPORTED";
        ClearActiveSeat(formation, task.ExecutorSeatId);
    }

    public static void MarkDeliveryUnknown(
        FormationData formation,
        string taskId,
        string reason)
    {
        var task = RequireRuntimeTask(formation, taskId);
        var attempt = task.Attempts.LastOrDefault(item => item.CompletedAt is null)
            ?? throw Conflict("没有可标记结果未知的派发尝试。", "missing-attempt", "attempt");
        if (attempt.State is not AttemptStates.Dispatching and not AttemptStates.Running)
        {
            throw Conflict("当前 Attempt 不能标记 DeliveryUnknown。", attempt.State, "state");
        }

        var now = DateTimeOffset.UtcNow;
        attempt.State = AttemptStates.DeliveryUnknown;
        attempt.CompletedAt = now;
        attempt.FailureCode = string.IsNullOrWhiteSpace(reason)
            ? "DELIVERY_UNKNOWN"
            : reason;
        task.State = FormationTaskStates.DeliveryUnknown;
        task.UpdatedAt = now;
        formation.Status = FormationStatuses.Blocked;
        formation.BlockedReason = "DELIVERY_UNKNOWN";
    }

    public static void CancelTask(FormationData formation, string taskId)
    {
        if (formation.Status is not FormationStatuses.InProgress and not FormationStatuses.Blocked)
        {
            throw Conflict("Formation 当前状态不允许取消任务。", formation.Status, "status");
        }

        var task = RequireTask(formation, taskId);
        if (task.State is FormationTaskStates.Passed
            or FormationTaskStates.Cancelled
            or FormationTaskStates.Unsupported
            or FormationTaskStates.DeliveryUnknown
            or FormationTaskStates.CancelUnconfirmed)
        {
            throw Conflict("任务当前状态不允许取消。", task.State, "state");
        }

        if (task.State is FormationTaskStates.Running or FormationTaskStates.CancelRequested)
        {
            var attempt = task.Attempts.LastOrDefault(item => item.CompletedAt is null)
                ?? throw Conflict("Running 任务缺少活动 Attempt。", "missing-attempt", "attempt");
            attempt.State = AttemptStates.CancelRequested;
            attempt.CancellationRequestedAt ??= DateTimeOffset.UtcNow;
            task.State = FormationTaskStates.CancelRequested;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            if (string.Equals(formation.Status, FormationStatuses.InProgress, StringComparison.Ordinal))
            {
                formation.Status = FormationStatuses.CancelRequested;
                formation.BlockedReason = "CANCELLATION_REQUESTED";
            }

            return;
        }

        CancelAttempts(task, TaskCancelled);
        task.State = FormationTaskStates.Cancelled;
        task.UpdatedAt = DateTimeOffset.UtcNow;
        ClearActiveSeat(formation, task.ExecutorSeatId);
    }

    public static void ConfirmTaskCancellation(
        FormationData formation,
        string taskId,
        string cancellationEvidence)
    {
        if (formation.Status is not FormationStatuses.InProgress
            and not FormationStatuses.CancelRequested)
        {
            throw Conflict("Formation 当前状态不允许确认取消。", formation.Status, "status");
        }

        var task = RequireTask(formation, taskId);
        if (!string.Equals(task.State, FormationTaskStates.CancelRequested, StringComparison.Ordinal))
        {
            throw Conflict("任务没有待确认的取消请求。", task.State, "state");
        }

        var attempt = task.Attempts.LastOrDefault(item =>
            string.Equals(item.State, AttemptStates.CancelRequested, StringComparison.Ordinal))
            ?? throw Conflict("没有待确认的取消 Attempt。", taskId, "attempt");
        attempt.State = AttemptStates.Cancelled;
        attempt.CompletedAt = DateTimeOffset.UtcNow;
        attempt.FailureCode = "TASK_CANCELLED_CONFIRMED";
        attempt.CancellationEvidence = cancellationEvidence;
        task.State = FormationTaskStates.Cancelled;
        task.UpdatedAt = DateTimeOffset.UtcNow;
        ClearActiveSeat(formation, task.ExecutorSeatId);
        if (string.Equals(formation.Status, FormationStatuses.CancelRequested, StringComparison.Ordinal)
            && formation.Tasks.All(item =>
                string.Equals(item.State, FormationTaskStates.Cancelled, StringComparison.Ordinal)
                || string.Equals(item.State, FormationTaskStates.Passed, StringComparison.Ordinal)))
        {
            formation.Status = FormationStatuses.Cancelled;
            formation.BlockedReason = "TASK_CANCELLATION_CONFIRMED";
        }
    }

    public static void CancelFormation(FormationData formation)
    {
        if (formation.Status is not FormationStatuses.InProgress
            and not FormationStatuses.Blocked
            and not FormationStatuses.CancelRequested)
        {
            throw Conflict("Formation 当前状态不允许取消。", formation.Status, "status");
        }

        foreach (var task in formation.Tasks.Where(task =>
                     task.State is not FormationTaskStates.Passed
                         and not FormationTaskStates.Cancelled
                         and not FormationTaskStates.Unsupported
                         and not FormationTaskStates.DeliveryUnknown))
        {
            if (task.State is FormationTaskStates.Running or FormationTaskStates.CancelRequested)
            {
                var attempt = task.Attempts.LastOrDefault(item => item.CompletedAt is null);
                if (attempt is not null)
                {
                    attempt.State = AttemptStates.CancelRequested;
                    attempt.CancellationRequestedAt ??= DateTimeOffset.UtcNow;
                }

                task.State = FormationTaskStates.CancelRequested;
                task.UpdatedAt = DateTimeOffset.UtcNow;
                continue;
            }

            CancelAttempts(task, FormationCancelled);
            task.State = FormationTaskStates.Cancelled;
            task.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var awaitingConfirmation = formation.Tasks.Any(item =>
            string.Equals(item.State, FormationTaskStates.CancelRequested, StringComparison.Ordinal));
        formation.Status = awaitingConfirmation
            ? FormationStatuses.CancelRequested
            : FormationStatuses.Cancelled;
        formation.BlockedReason = awaitingConfirmation
            ? "CANCELLATION_REQUESTED"
            : FormationCancelled;
    }

    public static void Archive(FormationData formation)
    {
        if (formation.Status is not FormationStatuses.Completed
            and not FormationStatuses.Cancelled)
        {
            throw Conflict(
                "只有 Completed 或 Cancelled Formation 可以归档。",
                formation.Status,
                "status");
        }

        formation.Status = FormationStatuses.Archived;
    }

    public static FormationAttemptData RequireActiveAttempt(
        FormationTaskData task,
        string expectedState)
    {
        var attempt = task.Attempts.LastOrDefault();
        if (attempt is null
            || attempt.CompletedAt is not null
            || !string.Equals(attempt.State, expectedState, StringComparison.Ordinal))
        {
            throw Conflict("没有符合当前状态的派发尝试。", attempt?.State ?? "missing", "attempt");
        }

        return attempt;
    }

    private static void CancelAttempts(FormationTaskData task, string reason)
    {
        foreach (var attempt in task.Attempts.Where(attempt => attempt.CompletedAt is null))
        {
            attempt.State = AttemptStates.Cancelled;
            attempt.CompletedAt = DateTimeOffset.UtcNow;
            attempt.FailureCode = reason;
        }
    }

    private static void ClearActiveSeat(FormationData formation, string seatId)
    {
        var seat = formation.Seats.FirstOrDefault(item =>
            string.Equals(item.Id, seatId, StringComparison.Ordinal));
        if (seat is not null)
        {
            seat.ActiveTaskId = string.Empty;
            seat.AgentId = string.Empty;
            seat.SessionId = string.Empty;
        }
    }

    private static FormationTaskData RequireRuntimeTask(
        FormationData formation,
        string taskId)
    {
        EnsureFormationStatus(
            formation,
            FormationStatuses.InProgress,
            "Formation 当前状态不允许记录运行时结果。");
        var task = RequireTask(formation, taskId);
        if (task.State is not FormationTaskStates.DispatchRequested
            and not FormationTaskStates.Running)
        {
            throw Conflict(
                "任务不处于可记录运行时终态的阶段。",
                task.State,
                "state");
        }

        return task;
    }

    private static void EnsureFormationStatus(
        FormationData formation,
        string expected,
        string message)
    {
        if (!string.Equals(formation.Status, expected, StringComparison.Ordinal))
        {
            throw Conflict(message, formation.Status, "status");
        }
    }

    private static FormationServiceException Conflict(
        string message,
        string value,
        string field) =>
        new(
            ErrorCodes.PreconditionFailed,
            message,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["field"] = field,
                ["value"] = value
            });

    private static FormationServiceException NotFound(string kind, string id) =>
        new(
            ErrorCodes.NotFound,
            kind + " 不存在。",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = id });
}
