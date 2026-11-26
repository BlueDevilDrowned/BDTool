using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BDTool.Contracts;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Formation;
using BDTool.Contracts.Operations;
using BDTool.Orchestration;
using BDTool.Tests.Support;

namespace BDTool.Tests.Formation;

/// <summary>
/// M7.1 文件持久化、人工状态机、requestId 幂等和路径范围的端到端测试。
/// 所有状态都写入 E 盘独立测试区，不使用用户项目或系统临时目录。
/// </summary>
public sealed class FormationServiceTests
{
    [Fact]
    public async Task Create_IsPersistentAndReplayRequiresIdenticalPayload()
    {
        using var project = new FormationTestProject();
        var request = new CreateFormationRequest
        {
            FormationId = "formation-main",
            Objective = "完成人工编队",
            RequestId = "create-1"
        };
        var firstService = new FileFormationService(project.ProjectPath);

        var created = await firstService.CreateFormationAsync(
            request,
            CancellationToken.None);

        AssertCompleted(created);
        Assert.False(created.Data.Replayed);
        Assert.Equal("create-1", created.Data.RequestId);
        Assert.Empty(created.Data.Formation.RequestReceipts);
        var statePath = GetStatePath(project.ProjectPath, request.FormationId);
        var firstHash = FileHash(statePath);

        var invalidRequest = await firstService.CreateFormationAsync(
            new CreateFormationRequest
            {
                FormationId = request.FormationId,
                Objective = request.Objective,
                RequestId = "   "
            },
            CancellationToken.None);
        AssertFailed(invalidRequest, ErrorCodes.InvalidArgument);
        Assert.Equal(firstHash, FileHash(statePath));

        var reopened = await new FileFormationService(project.ProjectPath)
            .GetFormationAsync(
                request.FormationId,
                includeReceipts: true,
                CancellationToken.None);
        AssertCompleted(reopened);
        var receipt = Assert.Single(reopened.Data.RequestReceipts).Value;
        Assert.Equal(64, receipt.PayloadSha256.Length);

        var replay = await new FileFormationService(project.ProjectPath)
            .CreateFormationAsync(request, CancellationToken.None);
        AssertCompleted(replay);
        Assert.True(replay.Data.Replayed);
        Assert.Equal(firstHash, FileHash(statePath));

        var conflict = await new FileFormationService(project.ProjectPath)
            .CreateFormationAsync(
                new CreateFormationRequest
                {
                    FormationId = request.FormationId,
                    Objective = "不同载荷",
                    RequestId = request.RequestId
                },
                CancellationToken.None);
        AssertFailed(conflict, ErrorCodes.PreconditionFailed);
        Assert.Equal(firstHash, FileHash(statePath));
    }

    [Fact]
    public async Task ManualLifecycle_ReviewAndRework_PreservesHistoryAndCompletes()
    {
        using var project = new FormationTestProject();
        var scenario = await CreateScenarioAsync(project.ProjectPath);

        var dispatched = await scenario.Service.DispatchTaskAsync(
            TaskCommand(scenario.FormationId, scenario.TaskId, "dispatch-1"),
            CancellationToken.None);
        AssertCompleted(dispatched);
        var activeSeat = Assert.Single(dispatched.Data.Formation.Seats,
            seat => seat.Id == scenario.ExecutorSeatId);
        Assert.Equal(scenario.TaskId, activeSeat.ActiveTaskId);

        var payloadConflict = await scenario.Service.DispatchTaskAsync(
            new TaskCommandRequest
            {
                FormationId = scenario.FormationId,
                TaskId = scenario.TaskId,
                RequestId = "dispatch-1",
                ActorId = "different-actor"
            },
            CancellationToken.None);
        AssertFailed(payloadConflict, ErrorCodes.PreconditionFailed);

        var started = await scenario.Service.StartManualTaskAsync(
            TaskCommand(scenario.FormationId, scenario.TaskId, "start-1"),
            CancellationToken.None);
        AssertCompleted(started);
        Assert.Equal(
            FormationTaskStates.ManualInProgress,
            started.Data.Formation.Tasks[0].State);

        var artifactSha = new string('a', 64);
        var submitted = await scenario.Service.SubmitTaskAsync(
            new SubmitFormationTaskRequest
            {
                FormationId = scenario.FormationId,
                TaskId = scenario.TaskId,
                RequestId = "submit-1",
                ActorId = "implementer",
                Summary = "第一轮已完成",
                ChangedPaths = new List<string> { "Assets/Scope/File.cs" },
                Artifacts = new List<FormationArtifactData>
                {
                    new()
                    {
                        Kind = "text",
                        Path = ".bdtool/artifacts/evidence.md",
                        Sha256 = artifactSha
                    }
                }
            },
            CancellationToken.None);
        AssertCompleted(submitted);
        var firstSubmission = Assert.Single(submitted.Data.Formation.Tasks[0].Submissions);
        Assert.Equal(FormationActorKinds.Human, firstSubmission.ActorKind);
        Assert.Equal("implementer", firstSubmission.ActorId);
        Assert.Single(submitted.Data.Formation.Artifacts);

        var failedReview = await scenario.Service.ReviewTaskAsync(
            new ReviewFormationTaskRequest
            {
                FormationId = scenario.FormationId,
                TaskId = scenario.TaskId,
                SubmissionId = firstSubmission.Id,
                RequestId = "review-1",
                ActorId = "reviewer",
                Passed = false,
                Issues = new List<FormationReviewIssueData>
                {
                    new()
                    {
                        Severity = ReviewSeverities.Error,
                        Message = "缺少边界测试",
                        Evidence = ".bdtool/artifacts/evidence.md"
                    }
                },
                MissingEvidence = new List<string> { "边界测试" },
                ReworkInstruction = "补充边界测试"
            },
            CancellationToken.None);
        AssertCompleted(failedReview);
        Assert.Equal(
            FormationTaskStates.NeedsRework,
            failedReview.Data.Formation.Tasks[0].State);
        Assert.Single(failedReview.Data.Formation.Tasks[0].Reviews);

        AssertCompleted(await scenario.Service.PrepareReworkAsync(
            TaskCommand(scenario.FormationId, scenario.TaskId, "rework-1"),
            CancellationToken.None));
        AssertCompleted(await scenario.Service.DispatchTaskAsync(
            TaskCommand(scenario.FormationId, scenario.TaskId, "dispatch-2"),
            CancellationToken.None));
        AssertCompleted(await scenario.Service.StartManualTaskAsync(
            TaskCommand(scenario.FormationId, scenario.TaskId, "start-2"),
            CancellationToken.None));

        var submittedAgain = await scenario.Service.SubmitTaskAsync(
            new SubmitFormationTaskRequest
            {
                FormationId = scenario.FormationId,
                TaskId = scenario.TaskId,
                RequestId = "submit-2",
                ActorId = "implementer",
                Summary = "补充边界测试",
                ChangedPaths = new List<string> { "Assets/Scope/File.cs" }
            },
            CancellationToken.None);
        AssertCompleted(submittedAgain);
        var task = submittedAgain.Data.Formation.Tasks[0];
        Assert.Equal(2, task.Attempts.Count);
        Assert.Equal(2, task.Submissions.Count);
        Assert.Single(task.Reviews);

        var passed = await scenario.Service.ReviewTaskAsync(
            new ReviewFormationTaskRequest
            {
                FormationId = scenario.FormationId,
                TaskId = scenario.TaskId,
                SubmissionId = task.Submissions[^1].Id,
                RequestId = "review-2",
                ActorId = "reviewer",
                Passed = true
            },
            CancellationToken.None);
        AssertCompleted(passed);
        Assert.Equal(FormationStatuses.Completed, passed.Data.Formation.Status);
        Assert.Equal(FormationTaskStates.Passed, passed.Data.Formation.Tasks[0].State);
        Assert.Equal(2, passed.Data.Formation.Tasks[0].Reviews.Count);
        Assert.All(passed.Data.Formation.Seats, seat =>
        {
            Assert.Empty(seat.AgentId);
            Assert.Empty(seat.SessionId);
            Assert.Empty(seat.ActiveTaskId);
        });
        Assert.DoesNotContain(
            passed.Data.Formation.Tasks[0].Attempts,
            attempt => attempt.State == AttemptStates.Running);
    }

    [Fact]
    public async Task Review_RejectsOldSubmission_AndBlocksAfterRetryLimit()
    {
        using var project = new FormationTestProject();
        var scenario = await CreateScenarioAsync(project.ProjectPath, reviewRetryMax: 0);

        var first = await CompleteManualSubmissionAsync(
            scenario,
            round: 1,
            changedPath: "Assets/Scope/File.cs");
        var firstSubmissionId = first.Tasks[0].Submissions[0].Id;
        var failedReview = await scenario.Service.ReviewTaskAsync(
            new ReviewFormationTaskRequest
            {
                FormationId = scenario.FormationId,
                TaskId = scenario.TaskId,
                SubmissionId = firstSubmissionId,
                RequestId = "review-old-limit",
                Passed = false,
                ReworkInstruction = "返工"
            },
            CancellationToken.None);
        AssertCompleted(failedReview);
        Assert.Equal(FormationTaskStates.Blocked, failedReview.Data.Formation.Tasks[0].State);
        Assert.Equal(FormationStatuses.Blocked, failedReview.Data.Formation.Status);
        Assert.Equal("REVIEW_RETRY_EXCEEDED", failedReview.Data.Formation.BlockedReason);
        Assert.All(failedReview.Data.Formation.Seats, seat => Assert.Empty(seat.ActiveTaskId));

        var blockedDispatch = await scenario.Service.DispatchTaskAsync(
            TaskCommand(scenario.FormationId, scenario.TaskId, "blocked-dispatch"),
            CancellationToken.None);
        AssertFailed(blockedDispatch, ErrorCodes.PreconditionFailed);
    }

    [Fact]
    public async Task Review_RejectsHistoricalSubmissionAfterRework()
    {
        using var project = new FormationTestProject();
        var scenario = await CreateScenarioAsync(project.ProjectPath);
        var first = await CompleteManualSubmissionAsync(
            scenario,
            round: 1,
            changedPath: "Assets/Scope/File.cs");
        var oldSubmissionId = first.Tasks[0].Submissions[0].Id;
        AssertCompleted(await scenario.Service.ReviewTaskAsync(
            new ReviewFormationTaskRequest
            {
                FormationId = scenario.FormationId,
                TaskId = scenario.TaskId,
                SubmissionId = oldSubmissionId,
                RequestId = "review-rework-1",
                Passed = false,
                ReworkInstruction = "继续修改"
            },
            CancellationToken.None));
        AssertCompleted(await scenario.Service.PrepareReworkAsync(
            TaskCommand(scenario.FormationId, scenario.TaskId, "rework-old"),
            CancellationToken.None));
        var second = await CompleteManualSubmissionAsync(
            scenario,
            round: 2,
            changedPath: "Assets/Scope/File.cs");

        var oldReview = await scenario.Service.ReviewTaskAsync(
            new ReviewFormationTaskRequest
            {
                FormationId = scenario.FormationId,
                TaskId = scenario.TaskId,
                SubmissionId = oldSubmissionId,
                RequestId = "review-old-submission",
                Passed = true
            },
            CancellationToken.None);
        AssertFailed(oldReview, ErrorCodes.PreconditionFailed);

        var latestReview = await scenario.Service.ReviewTaskAsync(
            new ReviewFormationTaskRequest
            {
                FormationId = scenario.FormationId,
                TaskId = scenario.TaskId,
                SubmissionId = second.Tasks[0].Submissions[^1].Id,
                RequestId = "review-latest",
                Passed = true
            },
            CancellationToken.None);
        AssertCompleted(latestReview);
        Assert.Equal(FormationStatuses.Completed, latestReview.Data.Formation.Status);
    }

    [Fact]
    public async Task ScopeValidation_RejectsOverlapTraversalAdsReservedAndChangedPathEscape()
    {
        using var project = new FormationTestProject();
        var service = await CreateFormationOnlyAsync(project.ProjectPath);

        AssertCompleted(await service.AddSeatAsync(
            new AddSeatRequest
            {
                FormationId = "formation-main",
                SeatId = "executor-a",
                Role = SeatRoles.Executor,
                DisplayName = "A",
                Access = WorkspaceAccesses.Write,
                WritePaths = new List<string> { "Assets/Shared" },
                RequestId = "seat-a"
            },
            CancellationToken.None));
        var overlap = await service.AddSeatAsync(
            new AddSeatRequest
            {
                FormationId = "formation-main",
                SeatId = "executor-b",
                Role = SeatRoles.Executor,
                DisplayName = "B",
                Access = WorkspaceAccesses.Write,
                WritePaths = new List<string> { "assets/shared/Sub" },
                RequestId = "seat-b"
            },
            CancellationToken.None);
        if (OperatingSystem.IsWindows())
        {
            AssertFailed(overlap, ErrorCodes.PreconditionFailed);
        }
        else
        {
            AssertCompleted(overlap);
        }

        AssertCompleted(await service.AddSeatAsync(
            new AddSeatRequest
            {
                FormationId = "formation-main",
                SeatId = "reviewer",
                Role = SeatRoles.Reviewer,
                DisplayName = "R",
                Access = WorkspaceAccesses.Read,
                RequestId = "reviewer"
            },
            CancellationToken.None));

        foreach (var (taskId, path, requestId) in new[]
                 {
                     ("task-traversal", "../outside", "task-1"),
                     ("task-ads", "Assets/a:b", "task-2"),
                     ("task-reserved", "Assets/CON.txt", "task-3"),
                     ("task-state", ".bdtool/state", "task-4"),
                     ("task-outside", Path.Combine(project.ProjectPath, "..", "outside"), "task-5")
                 })
        {
            var invalid = await service.CreateTaskAsync(
                TaskRequest(
                    "formation-main",
                    taskId,
                    path,
                    requestId,
                    reviewRetryMax: 2),
                CancellationToken.None);
            Assert.NotEqual(OperationStatus.Completed, invalid.Status);
        }

        AssertCompleted(await service.CreateTaskAsync(
            TaskRequest(
                "formation-main",
                "task-valid",
                "Assets/Shared/Allowed.cs",
                "task-valid",
                reviewRetryMax: 2),
            CancellationToken.None));
        var scenario = new FormationScenario(
            service,
            "formation-main",
            "task-valid",
            "executor-a",
            "reviewer");
        AssertCompleted(await service.DispatchTaskAsync(
            TaskCommand("formation-main", "task-valid", "dispatch-scope"),
            CancellationToken.None));
        AssertCompleted(await service.StartManualTaskAsync(
            TaskCommand("formation-main", "task-valid", "start-scope"),
            CancellationToken.None));
        var changedEscapeHash = FileHash(GetStatePath(project.ProjectPath, "formation-main"));
        var changedEscape = await service.SubmitTaskAsync(
            new SubmitFormationTaskRequest
            {
                FormationId = "formation-main",
                TaskId = "task-valid",
                RequestId = "changed-escape",
                Summary = "越界",
                ChangedPaths = new List<string> { "Assets/Other.cs" }
            },
            CancellationToken.None);
        AssertFailed(changedEscape, ErrorCodes.PreconditionFailed);
        Assert.Equal(changedEscapeHash, FileHash(GetStatePath(project.ProjectPath, "formation-main")));
        AssertCompleted(await service.SubmitTaskAsync(
            new SubmitFormationTaskRequest
            {
                FormationId = "formation-main",
                TaskId = "task-valid",
                RequestId = "changed-valid",
                Summary = "范围内修改",
                ChangedPaths = new List<string> { "Assets/Shared/Allowed.cs" }
            },
            CancellationToken.None));
    }

    [Fact]
    public async Task CorruptAndUnsupportedState_ReturnsStableCodeWithoutOverwrite()
    {
        using var project = new FormationTestProject();
        await CreateFormationOnlyAsync(project.ProjectPath);
        var path = GetStatePath(project.ProjectPath, "formation-main");

        await File.WriteAllTextAsync(path, "{broken", Encoding.UTF8);
        var corruptHash = FileHash(path);
        var corrupt = await new FileFormationService(project.ProjectPath)
            .GetFormationAsync("formation-main", includeReceipts: false, CancellationToken.None);
        AssertFailed(corrupt, "FORMATION_STATE_CORRUPT");
        Assert.Equal(corruptHash, FileHash(path));

        var unsupportedJson = JsonSerializer.Serialize(new FormationData
        {
            SchemaVersion = 2,
            Id = "formation-main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await File.WriteAllTextAsync(path, unsupportedJson, Encoding.UTF8);
        var unsupportedHash = FileHash(path);
        var unsupported = await new FileFormationService(project.ProjectPath)
            .GetFormationAsync("formation-main", includeReceipts: false, CancellationToken.None);
        AssertFailed(unsupported, "FORMATION_STATE_UNSUPPORTED");
        Assert.Equal(unsupportedHash, FileHash(path));
    }

    [Fact]
    public async Task CancelAndArchive_LeaveTerminalHistoryAndRejectFurtherMutation()
    {
        using var project = new FormationTestProject();
        var scenario = await CreateScenarioAsync(project.ProjectPath);

        var cancelled = await scenario.Service.CancelFormationAsync(
            new TaskCommandRequest
            {
                FormationId = scenario.FormationId,
                RequestId = "cancel-formation"
            },
            CancellationToken.None);
        AssertCompleted(cancelled);
        Assert.Equal(FormationStatuses.Cancelled, cancelled.Data.Formation.Status);
        Assert.Equal(FormationTaskStates.Cancelled, cancelled.Data.Formation.Tasks[0].State);

        var archived = await scenario.Service.ArchiveFormationAsync(
            new TaskCommandRequest
            {
                FormationId = scenario.FormationId,
                RequestId = "archive-formation"
            },
            CancellationToken.None);
        AssertCompleted(archived);
        Assert.Equal(FormationStatuses.Archived, archived.Data.Formation.Status);

        var rejected = await scenario.Service.DispatchTaskAsync(
            TaskCommand(scenario.FormationId, scenario.TaskId, "after-archive"),
            CancellationToken.None);
        AssertFailed(rejected, ErrorCodes.PreconditionFailed);
    }

    [Fact]
    public async Task ConcurrentReaders_ObserveOnlyCompleteAtomicState()
    {
        using var project = new FormationTestProject();
        var service = await CreateFormationOnlyAsync(project.ProjectPath);

        var writeTask = Task.Run(async () =>
        {
            for (var index = 0; index < 30; index++)
            {
                var result = await service.AddSeatAsync(
                    new AddSeatRequest
                    {
                        FormationId = "formation-main",
                        SeatId = "reviewer-" + index,
                        Role = SeatRoles.Reviewer,
                        DisplayName = "R" + index,
                        Access = WorkspaceAccesses.Read,
                        RequestId = "concurrent-seat-" + index
                    },
                    CancellationToken.None);
                AssertCompleted(result);
            }
        });

        var observed = new List<FormationData>();
        while (!writeTask.IsCompleted)
        {
            var read = await service.GetFormationAsync(
                "formation-main",
                includeReceipts: false,
                CancellationToken.None);
            AssertCompleted(read);
            observed.Add(read.Data);
            await Task.Yield();
        }

        await writeTask;
        var final = await service.GetFormationAsync(
            "formation-main",
            includeReceipts: false,
            CancellationToken.None);
        AssertCompleted(final);
        Assert.Equal(30, final.Data.Seats.Count);
        Assert.NotEmpty(observed);
        Assert.All(observed, item => Assert.Equal("formation-main", item.Id));
    }

    private static async Task<FileFormationService> CreateFormationOnlyAsync(string projectPath)
    {
        var service = new FileFormationService(projectPath);
        var result = await service.CreateFormationAsync(
            new CreateFormationRequest
            {
                FormationId = "formation-main",
                Objective = "测试编队",
                RequestId = "create-main"
            },
            CancellationToken.None);
        AssertCompleted(result);
        return service;
    }

    private static async Task<FormationScenario> CreateScenarioAsync(
        string projectPath,
        int reviewRetryMax = 2)
    {
        var service = await CreateFormationOnlyAsync(projectPath);
        AssertCompleted(await service.AddSeatAsync(
            new AddSeatRequest
            {
                FormationId = "formation-main",
                SeatId = "executor",
                Role = SeatRoles.Executor,
                DisplayName = "执行者",
                Access = WorkspaceAccesses.Write,
                WritePaths = new List<string> { "Assets/Scope" },
                RequestId = "add-executor"
            },
            CancellationToken.None));
        AssertCompleted(await service.AddSeatAsync(
            new AddSeatRequest
            {
                FormationId = "formation-main",
                SeatId = "reviewer",
                Role = SeatRoles.Reviewer,
                DisplayName = "审核者",
                Access = WorkspaceAccesses.Read,
                RequestId = "add-reviewer"
            },
            CancellationToken.None));
        AssertCompleted(await service.CreateTaskAsync(
            TaskRequest(
                "formation-main",
                "task-1",
                "Assets/Scope/File.cs",
                "add-task",
                reviewRetryMax,
                executorSeatId: "executor"),
            CancellationToken.None));
        return new FormationScenario(
            service,
            "formation-main",
            "task-1",
            "executor",
            "reviewer");
    }

    private static CreateFormationTaskRequest TaskRequest(
        string formationId,
        string taskId,
        string writePath,
        string requestId,
        int reviewRetryMax,
        string executorSeatId = "executor-a") =>
        new()
        {
            FormationId = formationId,
            TaskId = taskId,
            Title = "任务",
            Objective = "完成工作",
            Instructions = "只修改声明范围",
            Acceptance = "测试通过",
            ExecutorSeatId = executorSeatId,
            ReviewerSeatId = "reviewer",
            Access = WorkspaceAccesses.Write,
            WritePaths = new List<string> { writePath },
            ReviewRetryMax = reviewRetryMax,
            RequestId = requestId
        };

    private static TaskCommandRequest TaskCommand(
        string formationId,
        string taskId,
        string requestId) =>
        new()
        {
            FormationId = formationId,
            TaskId = taskId,
            RequestId = requestId
        };

    private static async Task<FormationData> CompleteManualSubmissionAsync(
        FormationScenario scenario,
        int round,
        string changedPath)
    {
        var dispatched = await scenario.Service.DispatchTaskAsync(
            TaskCommand(scenario.FormationId, scenario.TaskId, "dispatch-" + round),
            CancellationToken.None);
        AssertCompleted(dispatched);
        var started = await scenario.Service.StartManualTaskAsync(
            TaskCommand(scenario.FormationId, scenario.TaskId, "start-" + round),
            CancellationToken.None);
        AssertCompleted(started);
        var submitted = await scenario.Service.SubmitTaskAsync(
            new SubmitFormationTaskRequest
            {
                FormationId = scenario.FormationId,
                TaskId = scenario.TaskId,
                RequestId = "submit-round-" + round,
                Summary = "第 " + round + " 轮",
                ChangedPaths = new List<string> { changedPath }
            },
            CancellationToken.None);
        AssertCompleted(submitted);
        return submitted.Data.Formation;
    }

    private static void AssertCompleted<T>(OperationResult<T> result)
    {
        Assert.True(
            result.Status == OperationStatus.Completed,
            result.Errors.Count == 0
                ? "结果未完成。"
                : result.Errors[0].Code + ": " + result.Errors[0].Message);
        Assert.Empty(result.Errors);
    }

    private static void AssertFailed<T>(OperationResult<T> result, string code)
    {
        Assert.NotEqual(OperationStatus.Completed, result.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal(code, error.Code);
    }

    private static string GetStatePath(string projectPath, string formationId) =>
        Path.Combine(
            projectPath,
            ".bdtool",
            "state",
            "formations",
            formationId + ".json");

    private static string FileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed record FormationScenario(
        FileFormationService Service,
        string FormationId,
        string TaskId,
        string ExecutorSeatId,
        string ReviewerSeatId);

}
