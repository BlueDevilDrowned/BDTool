using BDTool.Contracts;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Formation;
using BDTool.Orchestration;

namespace BDTool.Tests.Formation;

/// <summary>
/// M7.2/M7.3 运行时身份契约：应用只生成叶子派发包，必须回填宿主真实 ID；
/// 预算、取消、Unspported 与 DeliveryUnknown 都在持久化状态中可追踪。
/// </summary>
public sealed class FormationRuntimeTests
{
    [Fact]
    public async Task NativeDispatch_RequiresRealBinding_AndCollectsAgentResult()
    {
        using var project = new FormationTestProject();
        var service = new FileFormationService(project.ProjectPath);
        await CreateRuntimeScenarioAsync(
            service,
            DispatchModes.Native,
            new FormationBudgetData { SubagentMax = 1, MaxDepth = 2 });

        var prepared = await service.PrepareTaskDispatchAsync(
            Prepare("native-1", DispatchModes.Native, userAuthorized: false),
            CancellationToken.None);
        AssertCompleted(prepared);
        var spawn = Assert.IsType<FormationSpawnRequestData>(prepared.Data.SpawnRequest);
        Assert.True(spawn.CanSpawn);
        Assert.True(spawn.LeafOnly);
        Assert.Equal(0, spawn.MaxSubagents);
        Assert.Equal(1, spawn.MaxDepth);
        Assert.Equal("bdtool-implementer", spawn.AgentProfile);
        Assert.Contains("禁止创建子智能体", spawn.Prompt, StringComparison.Ordinal);
        var task = prepared.Data.Formation.Tasks[0];
        Assert.Equal(FormationTaskStates.DispatchRequested, task.State);
        Assert.Empty(task.Attempts[0].AgentId);
        Assert.Empty(task.Attempts[0].SessionId);

        var replay = await service.PrepareTaskDispatchAsync(
            Prepare("native-1", DispatchModes.Native, userAuthorized: false),
            CancellationToken.None);
        AssertCompleted(replay);
        Assert.True(replay.Data.Replayed);
        Assert.Equal(spawn.AttemptId, replay.Data.SpawnRequest!.AttemptId);

        var missingEvidence = await service.BindTaskRuntimeAsync(
            new BindTaskRuntimeRequest
            {
                FormationId = "formation-runtime",
                TaskId = "task-runtime",
                RequestId = "bind-missing-evidence",
                AgentId = "agent-real-1",
                BindingEvidence = " "
            },
            CancellationToken.None);
        AssertFailed(missingEvidence, ErrorCodes.InvalidArgument);
        var stillWaiting = await service.GetFormationAsync(
            "formation-runtime",
            includeReceipts: false,
            CancellationToken.None);
        AssertCompleted(stillWaiting);
        Assert.Equal(
            FormationTaskStates.DispatchRequested,
            stillWaiting.Data.Tasks[0].State);

        var bound = await service.BindTaskRuntimeAsync(
            new BindTaskRuntimeRequest
            {
                FormationId = "formation-runtime",
                TaskId = "task-runtime",
                RequestId = "bind-agent-1",
                AgentId = "agent-real-1",
                BindingEvidence = "multi_agent_v1.spawn_agent returned agent_id=agent-real-1",
                HostAttestation = HostAttestation("spawn-agent-real-1")
            },
            CancellationToken.None);
        AssertCompleted(bound);
        var runningTask = bound.Data.Formation.Tasks[0];
        Assert.Equal(FormationTaskStates.Running, runningTask.State);
        Assert.Equal("agent-real-1", runningTask.Attempts[0].AgentId);
        Assert.Equal(
            "query_codex_thread returned spawn-agent-real-1",
            runningTask.Attempts[0].BindingEvidence);
        Assert.Equal("agent-real-1", bound.Data.Formation.Seats[0].AgentId);

        var submitted = await service.CollectTaskResultAsync(
            new CollectFormationTaskResultRequest
            {
                FormationId = "formation-runtime",
                TaskId = "task-runtime",
                RequestId = "agent-submit-1",
                Summary = "Agent 完成只读检查。",
                Unverified = new List<string> { "未运行性能测量" },
                HostResult = HostResult("agent-real-1")
            },
            CancellationToken.None);
        AssertCompleted(submitted);
        var submission = submitted.Data.Formation.Tasks[0].Submissions[0];
        Assert.Equal(FormationActorKinds.Coordinator, submission.ActorKind);
        Assert.Equal("coordinator", submission.ActorId);
        Assert.Equal("agent-real-1", submission.AttributedAgentId);
        Assert.NotNull(submission.HostResult);
        Assert.Equal(FormationTaskStates.Submitted, submitted.Data.Formation.Tasks[0].State);

        var reviewed = await service.ReviewTaskAsync(
            new ReviewFormationTaskRequest
            {
                FormationId = "formation-runtime",
                TaskId = "task-runtime",
                SubmissionId = submission.Id,
                Passed = true,
                RequestId = "review-native-1"
            },
            CancellationToken.None);
        AssertCompleted(reviewed);
        Assert.Equal(FormationStatuses.Completed, reviewed.Data.Formation.Status);
        Assert.Empty(reviewed.Data.Formation.Seats[0].AgentId);
        Assert.Empty(reviewed.Data.Formation.Seats[0].SessionId);
    }

    [Fact]
    public async Task NativeBudget_Unsupported_AndDeliveryUnknown_AreTerminalAndNotRetried()
    {
        using (var budgetProject = new FormationTestProject())
        {
            var service = new FileFormationService(budgetProject.ProjectPath);
            await CreateRuntimeScenarioAsync(
                service,
                DispatchModes.Native,
                new FormationBudgetData { SubagentMax = 1, MaxDepth = 2 },
                secondTask: true);
            AssertCompleted(await service.PrepareTaskDispatchAsync(
                Prepare("budget-first", DispatchModes.Native, userAuthorized: false),
                CancellationToken.None));
            var second = await service.PrepareTaskDispatchAsync(
                new PrepareTaskDispatchRequest
                {
                    FormationId = "formation-runtime",
                    TaskId = "task-runtime-2",
                    RequestId = "budget-second",
                    DispatchMode = DispatchModes.Native
                },
                CancellationToken.None);
            AssertFailed(second, ErrorCodes.PreconditionFailed);
        }

        using (var unsupportedProject = new FormationTestProject())
        {
            var service = new FileFormationService(unsupportedProject.ProjectPath);
            await CreateRuntimeScenarioAsync(
                service,
                DispatchModes.Native,
                new FormationBudgetData { SubagentMax = 1, MaxDepth = 2 });
            AssertCompleted(await service.PrepareTaskDispatchAsync(
                Prepare("unsupported-prepare", DispatchModes.Native, userAuthorized: false),
                CancellationToken.None));
            var unsupported = await service.MarkTaskUnsupportedAsync(
                new ReportTaskRuntimeFailureRequest
                {
                    FormationId = "formation-runtime",
                    TaskId = "task-runtime",
                    RequestId = "unsupported-result",
                    Reason = "Host Tools unavailable"
                },
                CancellationToken.None);
            AssertFailed(unsupported, ErrorCodes.CapabilityUnsupported);
            Assert.Equal(FormationTaskStates.Unsupported, unsupported.Data.Formation.Tasks[0].State);
            Assert.Equal("CAPABILITY_UNSUPPORTED", unsupported.Data.Formation.BlockedReason);
            AssertFailed(
                await service.PrepareTaskDispatchAsync(
                    Prepare("unsupported-retry", DispatchModes.Native, userAuthorized: false),
                    CancellationToken.None),
                ErrorCodes.PreconditionFailed);
        }

        using (var unknownProject = new FormationTestProject())
        {
            var service = new FileFormationService(unknownProject.ProjectPath);
            await CreateRuntimeScenarioAsync(
                service,
                DispatchModes.Native,
                new FormationBudgetData { SubagentMax = 1, MaxDepth = 2 });
            AssertCompleted(await service.PrepareTaskDispatchAsync(
                Prepare("unknown-prepare", DispatchModes.Native, userAuthorized: false),
                CancellationToken.None));
            AssertCompleted(await service.BindTaskRuntimeAsync(
                new BindTaskRuntimeRequest
                {
                    FormationId = "formation-runtime",
                    TaskId = "task-runtime",
                    RequestId = "unknown-bind",
                    AgentId = "agent-real-unknown",
                    BindingEvidence = "host result contains agent-real-unknown",
                    HostAttestation = HostAttestation("spawn-agent-unknown")
                },
                CancellationToken.None));
            var unknown = await service.MarkTaskDeliveryUnknownAsync(
                new ReportTaskRuntimeFailureRequest
                {
                    FormationId = "formation-runtime",
                    TaskId = "task-runtime",
                    RequestId = "unknown-result",
                    Reason = "Agent process disappeared"
                },
                CancellationToken.None);
            AssertFailed(unknown, ErrorCodes.ExecutionUncertain);
            Assert.Equal(
                FormationTaskStates.DeliveryUnknown,
                unknown.Data.Formation.Tasks[0].State);
            Assert.Equal("DELIVERY_UNKNOWN", unknown.Data.Formation.BlockedReason);
            Assert.Equal(
                "agent-real-unknown",
                unknown.Data.Formation.Seats[0].AgentId);
            AssertFailed(
                await service.CancelTaskAsync(
                    new TaskCommandRequest
                    {
                        FormationId = "formation-runtime",
                        TaskId = "task-runtime",
                        RequestId = "unknown-cancel-must-not-overwrite"
                    },
                    CancellationToken.None),
                ErrorCodes.PreconditionFailed);
            AssertFailed(
                await service.PrepareTaskDispatchAsync(
                    Prepare("unknown-retry", DispatchModes.Native, userAuthorized: false),
                    CancellationToken.None),
                ErrorCodes.PreconditionFailed);
        }
    }

    [Fact]
    public async Task RunningCancellation_RequiresHostConfirmationBeforeTerminalState()
    {
        using var project = new FormationTestProject();
        var service = new FileFormationService(project.ProjectPath);
        await CreateRuntimeScenarioAsync(
            service,
            DispatchModes.Native,
            new FormationBudgetData { SubagentMax = 1, MaxDepth = 2 });
        AssertCompleted(await service.PrepareTaskDispatchAsync(
            Prepare("cancel-prepare", DispatchModes.Native, userAuthorized: false),
            CancellationToken.None));
        AssertCompleted(await service.BindTaskRuntimeAsync(
            new BindTaskRuntimeRequest
            {
                FormationId = "formation-runtime",
                TaskId = "task-runtime",
                RequestId = "cancel-bind",
                AgentId = "agent-cancel-1",
                HostAttestation = HostAttestation("spawn-agent-cancel-1")
            },
            CancellationToken.None));

        var requested = await service.CancelTaskAsync(
            new TaskCommandRequest
            {
                FormationId = "formation-runtime",
                TaskId = "task-runtime",
                RequestId = "cancel-request"
            },
            CancellationToken.None);
        AssertCompleted(requested);
        Assert.Equal(FormationStatuses.CancelRequested, requested.Data.Formation.Status);
        Assert.Equal(
            FormationTaskStates.CancelRequested,
            requested.Data.Formation.Tasks[0].State);
        Assert.Equal(
            AttemptStates.CancelRequested,
            requested.Data.Formation.Tasks[0].Attempts[0].State);

        AssertFailed(
            await service.CollectTaskResultAsync(
                new CollectFormationTaskResultRequest
                {
                    FormationId = "formation-runtime",
                    TaskId = "task-runtime",
                    RequestId = "collect-after-cancel-request",
                    Summary = "must fail",
                    HostResult = HostResult("agent-cancel-1")
                },
                CancellationToken.None),
            ErrorCodes.PreconditionFailed);

        var confirmed = await service.ConfirmTaskCancellationAsync(
            new ConfirmTaskCancellationRequest
            {
                FormationId = "formation-runtime",
                TaskId = "task-runtime",
                RequestId = "cancel-confirm",
                CancellationEvidence = "host cancel API returned cancelled"
            },
            CancellationToken.None);
        AssertCompleted(confirmed);
        Assert.Equal(FormationStatuses.Cancelled, confirmed.Data.Formation.Status);
        Assert.Equal(FormationTaskStates.Cancelled, confirmed.Data.Formation.Tasks[0].State);
        Assert.Equal(
            "host cancel API returned cancelled",
            confirmed.Data.Formation.Tasks[0].Attempts[0].CancellationEvidence);
    }

    [Fact]
    public async Task SessionDispatch_RequiresExplicitAuthorizationAndRealSessionId()
    {
        using var project = new FormationTestProject();
        var service = new FileFormationService(project.ProjectPath);
        await CreateRuntimeScenarioAsync(
            service,
            DispatchModes.Session,
            new FormationBudgetData
            {
                SessionMax = 1,
                TreeSessionMax = 1,
                MaxDepth = 2
            });

        var unauthorized = await service.PrepareTaskDispatchAsync(
            Prepare("session-no-auth", DispatchModes.Session, userAuthorized: false),
            CancellationToken.None);
        AssertFailed(unauthorized, ErrorCodes.PreconditionFailed);
        Assert.Equal("userAuthorized", unauthorized.Errors[0].Details["field"]);

        var prepared = await service.PrepareTaskDispatchAsync(
            new PrepareTaskDispatchRequest
            {
                FormationId = "formation-runtime",
                TaskId = "task-runtime",
                RequestId = "session-authorized",
                DispatchMode = DispatchModes.Session,
                UserAuthorized = true,
                AuthorizationQuote = "用户明确授权创建独立 Session"
            },
            CancellationToken.None);
        AssertCompleted(prepared);
        Assert.True(prepared.Data.SpawnRequest!.UserAuthorizationRequired);
        Assert.Equal(DispatchModes.Session, prepared.Data.SpawnRequest.DispatchMode);
        Assert.Single(prepared.Data.Formation.SessionAuthorizations);
        Assert.Equal(
            prepared.Data.Formation.SessionAuthorizations[0].Id,
            prepared.Data.SpawnRequest.SessionAuthorizationId);

        var missingSession = await service.BindTaskRuntimeAsync(
            new BindTaskRuntimeRequest
            {
                FormationId = "formation-runtime",
                TaskId = "task-runtime",
                RequestId = "session-missing-id",
                SessionId = " ",
                BindingEvidence = "create_thread returned no id",
                HostAttestation = HostAttestation("create-session-missing")
            },
            CancellationToken.None);
        AssertFailed(missingSession, ErrorCodes.PreconditionFailed);

        var bound = await service.BindTaskRuntimeAsync(
            new BindTaskRuntimeRequest
            {
                FormationId = "formation-runtime",
                TaskId = "task-runtime",
                RequestId = "session-bind",
                SessionId = "session-real-1",
                BindingEvidence = "create_thread returned threadId=session-real-1",
                HostAttestation = HostAttestation("create-session-real-1")
            },
            CancellationToken.None);
        AssertCompleted(bound);
        Assert.Equal(FormationTaskStates.Running, bound.Data.Formation.Tasks[0].State);
        Assert.Equal(
            "session-real-1",
            bound.Data.Formation.Tasks[0].Attempts[0].SessionId);

        var submitted = await service.CollectTaskResultAsync(
            new CollectFormationTaskResultRequest
            {
                FormationId = "formation-runtime",
                TaskId = "task-runtime",
                RequestId = "session-submit",
                Summary = "独立 Session 返回只读结果。",
                HostResult = HostResult("session-real-1")
            },
            CancellationToken.None);
        AssertCompleted(submitted);
        var submission = submitted.Data.Formation.Tasks[0].Submissions[0];
        Assert.Equal(FormationActorKinds.Coordinator, submission.ActorKind);
        Assert.Equal("coordinator", submission.ActorId);
        Assert.Equal("session-real-1", submission.AttributedAgentId);

        AssertCompleted(await service.ReviewTaskAsync(
            new ReviewFormationTaskRequest
            {
                FormationId = "formation-runtime",
                TaskId = "task-runtime",
                SubmissionId = submission.Id,
                Passed = true,
                RequestId = "session-review"
            },
            CancellationToken.None));
    }

    [Fact]
    public async Task SessionBudgetZero_RejectsBeforeAnyStateChange()
    {
        using var project = new FormationTestProject();
        var service = new FileFormationService(project.ProjectPath);
        await CreateRuntimeScenarioAsync(
            service,
            DispatchModes.Session,
            new FormationBudgetData
            {
                SessionMax = 0,
                TreeSessionMax = 3,
                MaxDepth = 2
            });
        var path = Path.Combine(
            project.ProjectPath,
            ".bdtool",
            "state",
            "formations",
            "formation-runtime.json");
        var before = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

        var result = await service.PrepareTaskDispatchAsync(
            new PrepareTaskDispatchRequest
            {
                FormationId = "formation-runtime",
                TaskId = "task-runtime",
                RequestId = "session-budget-zero",
                DispatchMode = DispatchModes.Session,
                UserAuthorized = true,
                AuthorizationQuote = "用户明确授权创建独立 Session"
            },
            CancellationToken.None);

        AssertFailed(result, ErrorCodes.PreconditionFailed);
        var after = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(before, after);
    }

    private static async Task CreateRuntimeScenarioAsync(
        FileFormationService service,
        string dispatchMode,
        FormationBudgetData budget,
        bool secondTask = false)
    {
        AssertCompleted(await service.CreateFormationAsync(
            new CreateFormationRequest
            {
                FormationId = "formation-runtime",
                Objective = "运行时派发",
                RequestId = "runtime-create",
                Budget = budget
            },
            CancellationToken.None));
        AssertCompleted(await service.AddSeatAsync(
            new AddSeatRequest
            {
                FormationId = "formation-runtime",
                SeatId = "executor",
                Role = SeatRoles.Executor,
                DisplayName = "执行者",
                DispatchMode = dispatchMode,
                Access = WorkspaceAccesses.Read,
                RequestId = "runtime-executor"
            },
            CancellationToken.None));
        AssertCompleted(await service.AddSeatAsync(
            new AddSeatRequest
            {
                FormationId = "formation-runtime",
                SeatId = "reviewer",
                Role = SeatRoles.Reviewer,
                DisplayName = "审核者",
                Access = WorkspaceAccesses.Read,
                RequestId = "runtime-reviewer"
            },
            CancellationToken.None));
        AssertCompleted(await service.CreateTaskAsync(
            Task("task-runtime", "runtime-task-1"),
            CancellationToken.None));
        if (secondTask)
        {
            AssertCompleted(await service.CreateTaskAsync(
                Task("task-runtime-2", "runtime-task-2"),
                CancellationToken.None));
        }
    }

    private static CreateFormationTaskRequest Task(string taskId, string requestId) => new()
    {
        FormationId = "formation-runtime",
        TaskId = taskId,
        Title = "只读检查",
        Objective = "返回一项可验证结果",
        Instructions = "不得创建子智能体",
        Acceptance = "有明确证据",
        ExecutorSeatId = "executor",
        ReviewerSeatId = "reviewer",
        Access = WorkspaceAccesses.Read,
        RequestId = requestId
    };

    private static PrepareTaskDispatchRequest Prepare(
        string requestId,
        string dispatchMode,
        bool userAuthorized) => new()
        {
            FormationId = "formation-runtime",
            TaskId = "task-runtime",
            RequestId = requestId,
            DispatchMode = dispatchMode,
            UserAuthorized = userAuthorized
        };

    private static FormationHostAttestationData HostAttestation(string toolCallId) => new()
    {
        HostId = "local",
        ToolCallId = toolCallId,
        CapturedAt = DateTimeOffset.UtcNow,
        RawResultSha256 = new string('b', 64),
        Evidence = "query_codex_thread returned " + toolCallId
    };

    private static FormationHostResultReferenceData HostResult(string threadId) => new()
    {
        HostId = "local",
        ThreadId = threadId,
        TurnId = "turn-1",
        MessageId = "message-1",
        CollectedAt = DateTimeOffset.UtcNow,
        ContentSha256 = new string('c', 64),
        AttestedBy = "codex-coordinator"
    };

    private static void AssertCompleted<T>(
        BDTool.Contracts.Operations.OperationResult<T> result)
    {
        Assert.True(
            result.Status == OperationStatus.Completed,
            result.Errors.Count == 0
                ? "结果未完成。"
                : result.Errors[0].Code + ": " + result.Errors[0].Message);
        Assert.Empty(result.Errors);
    }

    private static void AssertFailed<T>(
        BDTool.Contracts.Operations.OperationResult<T> result,
        string code)
    {
        Assert.NotEqual(OperationStatus.Completed, result.Status);
        Assert.Equal(code, Assert.Single(result.Errors).Code);
    }
}
