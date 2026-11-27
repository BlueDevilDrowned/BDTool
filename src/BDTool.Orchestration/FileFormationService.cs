using System.Text.Json;
using BDTool.Contracts;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Formation;
using BDTool.Contracts.Operations;

namespace BDTool.Orchestration;

/// <summary>
/// M7 Formation 服务。服务只编排锁、读写和回执；输入规则由 ScopeValidator、
/// 生命周期迁移由 StateMachine 负责。原生派发只生成申请，不直接创建宿主 Agent。
/// </summary>
public sealed class FileFormationService : IFormationService
{
    private readonly FormationRepository _repository;
    private readonly FormationScopeValidator _validator;

    public FileFormationService(string projectRoot)
    {
        _repository = new FormationRepository(projectRoot);
        _validator = new FormationScopeValidator(projectRoot);
    }

    public Task<OperationResult<FormationCommandData>> CreateFormationAsync(
        CreateFormationRequest request,
        CancellationToken cancellationToken) =>
        ExecuteCreateAsync(request, cancellationToken);

    public Task<OperationResult<FormationCommandData>> AddSeatAsync(
        AddSeatRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            FormationRequestCommands.AddSeat,
            BuildEntityId(request.FormationId, "seat", request.SeatId),
            request,
            formation => AddSeat(formation, request),
            cancellationToken);

    public Task<OperationResult<FormationCommandData>> CreateTaskAsync(
        CreateFormationTaskRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            FormationRequestCommands.CreateTask,
            BuildEntityId(request.FormationId, "task", request.TaskId),
            request,
            formation => AddTask(formation, request),
            cancellationToken);

    public Task<OperationResult<FormationCommandData>> DispatchTaskAsync(
        TaskCommandRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            FormationRequestCommands.DispatchTask,
            BuildEntityId(request.FormationId, "task", request.TaskId),
            request,
            formation => DispatchTask(formation, request),
            cancellationToken);

    public Task<OperationResult<FormationCommandData>> StartManualTaskAsync(
        TaskCommandRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            FormationRequestCommands.StartTask,
            BuildEntityId(request.FormationId, "task", request.TaskId),
            request,
            formation =>
            {
                _validator.ValidateIdentifier(request.TaskId, "taskId");
                FormationStateMachine.StartManual(formation, request.TaskId);
            },
            cancellationToken);

    public Task<OperationResult<FormationCommandData>> SubmitTaskAsync(
        SubmitFormationTaskRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            FormationRequestCommands.SubmitTask,
            BuildEntityId(request.FormationId, "task", request.TaskId),
            request,
            formation => SubmitTask(formation, request),
            cancellationToken);

    public Task<OperationResult<FormationCommandData>> ReviewTaskAsync(
        ReviewFormationTaskRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            FormationRequestCommands.ReviewTask,
            BuildEntityId(request.FormationId, "task", request.TaskId),
            request,
            formation => ReviewTask(formation, request),
            cancellationToken);

    public Task<OperationResult<FormationCommandData>> PrepareReworkAsync(
        TaskCommandRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            FormationRequestCommands.PrepareRework,
            BuildEntityId(request.FormationId, "task", request.TaskId),
            request,
            formation =>
            {
                _validator.ValidateIdentifier(request.TaskId, "taskId");
                FormationStateMachine.PrepareRework(formation, request.TaskId);
            },
            cancellationToken);

    public Task<OperationResult<FormationCommandData>> PrepareTaskDispatchAsync(
        PrepareTaskDispatchRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            string.Equals(request.DispatchMode, DispatchModes.Session, StringComparison.Ordinal)
                ? FormationRequestCommands.PrepareSessionDispatch
                : FormationRequestCommands.PrepareNativeDispatch,
            BuildEntityId(request.FormationId, "task", request.TaskId),
            request,
            formation => PrepareRuntimeDispatch(formation, request),
            cancellationToken,
            formation => BuildSpawnRequest(formation, request));

    public Task<OperationResult<FormationCommandData>> BindTaskRuntimeAsync(
        BindTaskRuntimeRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            FormationRequestCommands.BindRuntimeIdentity,
            BuildEntityId(request.FormationId, "task", request.TaskId),
            request,
            formation => BindRuntime(formation, request),
            cancellationToken);

    public Task<OperationResult<FormationCommandData>> CollectTaskResultAsync(
        CollectFormationTaskResultRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            FormationRequestCommands.CollectResult,
            BuildEntityId(request.FormationId, "task", request.TaskId),
            request,
            formation => CollectRuntimeResult(formation, request),
            cancellationToken);

    public Task<OperationResult<FormationCommandData>> MarkTaskUnsupportedAsync(
        ReportTaskRuntimeFailureRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            FormationRequestCommands.MarkUnsupported,
            BuildEntityId(request.FormationId, "task", request.TaskId),
            request,
            formation =>
            {
                _validator.ValidateIdentifier(request.TaskId, "taskId");
                FormationStateMachine.MarkUnsupported(
                    formation,
                    request.TaskId,
                    request.Reason);
            },
            cancellationToken,
            terminalResult: formation => FailureCommand(
                formation,
                request.RequestId,
                ErrorCodes.CapabilityUnsupported,
                "Codex 宿主未提供所需的原生派发能力。",
                OperationStatus.Failed,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["taskId"] = request.TaskId,
                    ["reason"] = request.Reason
                }));

    public Task<OperationResult<FormationCommandData>> MarkTaskDeliveryUnknownAsync(
        ReportTaskRuntimeFailureRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            FormationRequestCommands.MarkDeliveryUnknown,
            BuildEntityId(request.FormationId, "task", request.TaskId),
            request,
            formation =>
            {
                _validator.ValidateIdentifier(request.TaskId, "taskId");
                FormationStateMachine.MarkDeliveryUnknown(
                    formation,
                    request.TaskId,
                    request.Reason);
            },
            cancellationToken,
            terminalResult: formation => FailureCommand(
                formation,
                request.RequestId,
                ErrorCodes.ExecutionUncertain,
                "Agent 结果无法确认；已记录 DeliveryUnknown，禁止自动重派。",
                OperationStatus.Uncertain,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["taskId"] = request.TaskId,
                    ["reason"] = request.Reason
                }));

    public Task<OperationResult<FormationCommandData>> CancelTaskAsync(
        TaskCommandRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            FormationRequestCommands.CancelTask,
            BuildEntityId(request.FormationId, "task", request.TaskId),
            request,
            formation =>
            {
                _validator.ValidateIdentifier(request.TaskId, "taskId");
                FormationStateMachine.CancelTask(formation, request.TaskId);
            },
            cancellationToken);

    public Task<OperationResult<FormationCommandData>> ConfirmTaskCancellationAsync(
        ConfirmTaskCancellationRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            FormationRequestCommands.ConfirmTaskCancellation,
            BuildEntityId(request.FormationId, "task", request.TaskId),
            request,
            formation =>
            {
                _validator.ValidateIdentifier(request.TaskId, "taskId");
                FormationStateMachine.ConfirmTaskCancellation(
                    formation,
                    request.TaskId,
                    _validator.RequireText(
                        request.CancellationEvidence,
                        "cancellationEvidence"));
            },
            cancellationToken);

    public Task<OperationResult<FormationCommandData>> CancelFormationAsync(
        TaskCommandRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            FormationRequestCommands.CancelFormation,
            request.FormationId,
            request,
            FormationStateMachine.CancelFormation,
            cancellationToken);

    public Task<OperationResult<FormationCommandData>> ArchiveFormationAsync(
        TaskCommandRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            request.FormationId,
            request.RequestId,
            FormationRequestCommands.ArchiveFormation,
            request.FormationId,
            request,
            FormationStateMachine.Archive,
            cancellationToken);

    public async Task<OperationResult<FormationData>> GetFormationAsync(
        string formationId,
        bool includeReceipts,
        CancellationToken cancellationToken)
    {
        try
        {
            _validator.ValidateIdentifier(formationId, "formationId");
            var formation = await _repository.ReadAsync(formationId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw NotFoundFormation(formationId);
            return Success(_repository.CloneForOutput(formation, includeReceipts));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<FormationData>(
                ErrorCodes.Cancelled,
                "Formation 查询已取消。",
                OperationStatus.Cancelled);
        }
        catch (FormationServiceException exception)
        {
            return Failure<FormationData>(
                exception.Code,
                exception.Message,
                details: exception.Details,
                retryable: exception.Retryable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Failure<FormationData>(
                ErrorCodes.ExecutionFailed,
                "Formation 读取失败：" + exception.Message);
        }
    }

    public async Task<OperationResult<FormationListData>> ListFormationsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var data = new FormationListData();
            var result = Success(data);
            foreach (var file in _repository.ListFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var formation = await _repository.ReadByPathAsync(file, cancellationToken)
                        .ConfigureAwait(false);
                    data.Formations.Add(ToSummary(formation));
                }
                catch (FormationServiceException exception)
                {
                    result.Warnings.Add(new KernelWarning
                    {
                        Code = "FORMATION_FILE_UNREADABLE",
                        Message = exception.Message,
                        Details = new Dictionary<string, string>(
                            exception.Details,
                            StringComparer.Ordinal)
                    });
                }
            }

            data.Formations.Sort((left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<FormationListData>(
                ErrorCodes.Cancelled,
                "Formation 列表查询已取消。",
                OperationStatus.Cancelled);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure<FormationListData>(
                ErrorCodes.ExecutionFailed,
                "Formation 列表读取失败：" + exception.Message);
        }
    }

    private async Task<OperationResult<FormationCommandData>> ExecuteCreateAsync(
        CreateFormationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            _validator.ValidateIdentifier(request.FormationId, "formationId");
            _validator.ValidateRequestId(request.RequestId);
            var objective = _validator.RequireText(request.Objective, "objective");
            var budget = _validator.ValidateBudget(request.Budget ?? new FormationBudgetData());
            var payloadSha256 = FormationRequestLedger.ComputePayloadSha256(request);

            using var formationLock = await _repository.AcquireLockAsync(
                    request.FormationId,
                    cancellationToken)
                .ConfigureAwait(false);
            var existing = await _repository.ReadAsync(request.FormationId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                if (FormationRequestLedger.HasMatchingReceipt(
                    existing,
                    request.RequestId,
                    FormationRequestCommands.Create,
                    request.FormationId,
                    payloadSha256))
                {
                    return SuccessCommand(existing, request.RequestId, replayed: true);
                }

                throw Conflict(
                    "Formation 已存在且 requestId 不匹配。",
                    request.FormationId,
                    "formationId");
            }

            var now = DateTimeOffset.UtcNow;
            var formation = new FormationData
            {
                Id = request.FormationId,
                Objective = objective,
                Budget = budget,
                CreatedAt = now,
                UpdatedAt = now
            };
            FormationRequestLedger.AddReceipt(
                formation,
                request.RequestId,
                FormationRequestCommands.Create,
                formation.Id,
                payloadSha256,
                now);
            await _repository.WriteAsync(formation, cancellationToken).ConfigureAwait(false);
            return SuccessCommand(formation, request.RequestId, replayed: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<FormationCommandData>(
                ErrorCodes.Cancelled,
                "Formation 创建已取消。",
                OperationStatus.Cancelled);
        }
        catch (FormationServiceException exception)
        {
            return Failure<FormationCommandData>(
                exception.Code,
                exception.Message,
                details: exception.Details,
                retryable: exception.Retryable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Failure<FormationCommandData>(
                ErrorCodes.ExecutionFailed,
                "Formation 写入失败：" + exception.Message);
        }
    }

    private async Task<OperationResult<FormationCommandData>> MutateAsync<TRequest>(
        string formationId,
        string requestId,
        string command,
        string entityId,
        TRequest request,
        Action<FormationData> mutation,
        CancellationToken cancellationToken,
        Func<FormationData, FormationCommandData>? commandFactory = null,
        Func<FormationData, OperationResult<FormationCommandData>>? terminalResult = null)
        where TRequest : class
    {
        try
        {
            _validator.ValidateIdentifier(formationId, "formationId");
            _validator.ValidateRequestId(requestId);
            var payloadSha256 = FormationRequestLedger.ComputePayloadSha256(request);

            using var formationLock = await _repository.AcquireLockAsync(formationId, cancellationToken)
                .ConfigureAwait(false);
            var formation = await _repository.ReadAsync(formationId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw NotFoundFormation(formationId);
            if (FormationRequestLedger.HasMatchingReceipt(
                formation,
                requestId,
                command,
                entityId,
                payloadSha256))
            {
                return SuccessCommand(
                    formation,
                    requestId,
                    replayed: true,
                    commandFactory);
            }

            FormationRequestLedger.EnsureUniqueRequestId(formation, requestId);
            mutation(formation);
            var now = DateTimeOffset.UtcNow;
            formation.UpdatedAt = now;
            FormationRequestLedger.AddReceipt(
                formation,
                requestId,
                command,
                entityId,
                payloadSha256,
                now);
            await _repository.WriteAsync(formation, cancellationToken).ConfigureAwait(false);
            return terminalResult?.Invoke(formation)
                ?? SuccessCommand(
                    formation,
                    requestId,
                    replayed: false,
                    commandFactory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<FormationCommandData>(
                ErrorCodes.Cancelled,
                "Formation 操作已取消。",
                OperationStatus.Cancelled);
        }
        catch (FormationServiceException exception)
        {
            return Failure<FormationCommandData>(
                exception.Code,
                exception.Message,
                details: exception.Details,
                retryable: exception.Retryable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Failure<FormationCommandData>(
                ErrorCodes.ExecutionFailed,
                "Formation 状态写入失败：" + exception.Message);
        }
    }

    private void AddSeat(FormationData formation, AddSeatRequest request)
    {
        FormationStateMachine.EnsureStructureMutable(formation);
        _validator.ValidateIdentifier(request.SeatId, "seatId");
        if (formation.Seats.Any(seat =>
                string.Equals(seat.Id, request.SeatId, StringComparison.Ordinal)))
        {
            throw Conflict("Seat 已存在。", request.SeatId, "seatId");
        }

        _validator.ValidateChoice(request.Role, SeatRoles.All, "role");
        _validator.ValidateChoice(request.Access, WorkspaceAccesses.All, "access");
        _validator.ValidateChoice(
            request.DispatchMode,
            new[] { DispatchModes.Manual, DispatchModes.Native, DispatchModes.Session },
            "dispatchMode");
        var displayName = _validator.RequireText(request.DisplayName, "displayName");
        if (!string.Equals(request.Role, SeatRoles.Executor, StringComparison.Ordinal)
            && string.Equals(request.Access, WorkspaceAccesses.Write, StringComparison.Ordinal))
        {
            throw Invalid("只有 Executor 席位可以声明写访问。", "role");
        }

        if (request.SubagentMax < 0 || request.SubagentMax > formation.Budget.SubagentMax)
        {
            throw Invalid(
                "seat subagentMax 必须在 0 到 formation.subagentMax 之间。",
                "subagentMax");
        }

        if (request.DispatchMode is DispatchModes.Native or DispatchModes.Session
            && request.SubagentMax != 0)
        {
            throw Invalid(
                "Native/Session 执行席位是叶子 Agent，subagentMax 必须为 0。",
                "subagentMax");
        }

        var writePaths = _validator.NormalizeWorkspace(
            request.Access,
            request.WritePaths,
            "seat workspace");
        _validator.EnsureNoSeatWriteOverlap(formation, request.SeatId, writePaths);
        formation.Seats.Add(new FormationSeatData
        {
            Id = request.SeatId,
            Role = request.Role,
            DisplayName = displayName,
            DispatchMode = request.DispatchMode,
            AgentId = string.Empty,
            SessionId = string.Empty,
            AgentProfile = request.AgentProfile ?? string.Empty,
            Model = request.Model ?? string.Empty,
            Effort = request.Effort ?? string.Empty,
            Workspace = new FormationWorkspaceData
            {
                Access = request.Access,
                WritePaths = writePaths
            },
            SubagentMax = request.SubagentMax,
            ActiveTaskId = string.Empty
        });
    }

    private void AddTask(FormationData formation, CreateFormationTaskRequest request)
    {
        FormationStateMachine.EnsureStructureMutable(formation);
        _validator.ValidateIdentifier(request.TaskId, "taskId");
        if (formation.Tasks.Any(task =>
                string.Equals(task.Id, request.TaskId, StringComparison.Ordinal)))
        {
            throw Conflict("Task 已存在。", request.TaskId, "taskId");
        }

        var title = _validator.RequireText(request.Title, "title");
        var objective = _validator.RequireText(request.Objective, "objective");
        var instructions = _validator.RequireText(request.Instructions, "instructions");
        var acceptance = _validator.RequireText(request.Acceptance, "acceptance");
        _validator.ValidateIdentifier(request.ExecutorSeatId, "executorSeatId");
        _validator.ValidateIdentifier(request.ReviewerSeatId, "reviewerSeatId");
        var executor = FormationStateMachine.RequireSeat(
            formation,
            request.ExecutorSeatId,
            SeatRoles.Executor);
        var reviewer = FormationStateMachine.RequireSeat(
            formation,
            request.ReviewerSeatId,
            SeatRoles.Reviewer);
        var writePaths = _validator.NormalizeWorkspace(
            request.Access,
            request.WritePaths,
            "task workspace");
        _validator.EnsureWritePathsWithinSeat(executor, request.Access, writePaths);
        _validator.EnsureNoTaskWriteOverlap(
            formation,
            request.TaskId,
            request.Access,
            writePaths);
        if (request.ReviewRetryMax < 0
            || request.ReviewRetryMax > formation.Budget.ReviewRetryMax)
        {
            throw Invalid(
                "task reviewRetryMax 必须在 0 到 formation.reviewRetryMax 之间。",
                "reviewRetryMax");
        }

        var now = DateTimeOffset.UtcNow;
        formation.Tasks.Add(new FormationTaskData
        {
            Id = request.TaskId,
            Title = title,
            Objective = objective,
            Instructions = instructions,
            Acceptance = acceptance,
            ExecutorSeatId = executor.Id,
            ReviewerSeatId = reviewer.Id,
            Workspace = new FormationWorkspaceData
            {
                Access = request.Access,
                WritePaths = writePaths
            },
            ReviewRetryMax = request.ReviewRetryMax,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private void DispatchTask(FormationData formation, TaskCommandRequest request)
    {
        _validator.ValidateIdentifier(request.TaskId, "taskId");
        var actorId = _validator.RequireActorId(request.ActorId);
        FormationStateMachine.Dispatch(
            formation,
            request.TaskId,
            request.RequestId,
            actorId);
    }

    private void SubmitTask(
        FormationData formation,
        SubmitFormationTaskRequest request)
    {
        _validator.ValidateIdentifier(request.TaskId, "taskId");
        var actorId = _validator.RequireActorId(request.ActorId);
        var task = FormationStateMachine.RequireTask(formation, request.TaskId);
        var attempt = FormationStateMachine.RequireActiveAttempt(
            task,
            AttemptStates.ManualInProgress);

        var now = DateTimeOffset.UtcNow;
        var submission = new FormationSubmissionData
        {
            Id = FormationRepository.NextId("submission"),
            AttemptId = attempt.Id,
            RequestId = request.RequestId,
            ActorKind = FormationActorKinds.Human,
            ActorId = actorId,
            Summary = _validator.RequireText(request.Summary, "summary"),
            ChangedPaths = _validator.NormalizeChangedPaths(task, request.ChangedPaths),
            Artifacts = _validator.ValidateArtifacts(request.Artifacts),
            Unverified = _validator.CleanStrings(request.Unverified),
            CreatedAt = now
        };
        FormationStateMachine.Submit(formation, request.TaskId, submission);
    }

    private void CollectRuntimeResult(
        FormationData formation,
        CollectFormationTaskResultRequest request)
    {
        _validator.ValidateIdentifier(request.TaskId, "taskId");
        var coordinatorId = _validator.RequireActorId(request.ActorId);
        var task = FormationStateMachine.RequireTask(formation, request.TaskId);
        var attempt = FormationStateMachine.RequireActiveAttempt(task, AttemptStates.Running);
        var attributedAgentId = string.IsNullOrWhiteSpace(attempt.AgentId)
            ? attempt.SessionId
            : attempt.AgentId;
        var hostResult = ValidateHostResult(request.HostResult);
        if (!string.Equals(hostResult.ThreadId, attributedAgentId, StringComparison.Ordinal))
        {
            throw Conflict(
                "HostResult.ThreadId 与绑定身份不一致。",
                hostResult.ThreadId,
                "hostResult.threadId");
        }

        var now = DateTimeOffset.UtcNow;
        var submission = new FormationSubmissionData
        {
            Id = FormationRepository.NextId("submission"),
            AttemptId = attempt.Id,
            RequestId = request.RequestId,
            ActorKind = FormationActorKinds.Coordinator,
            ActorId = coordinatorId,
            AttributedAgentId = attributedAgentId,
            HostResult = hostResult,
            Summary = _validator.RequireText(request.Summary, "summary"),
            ChangedPaths = _validator.NormalizeChangedPaths(task, request.ChangedPaths),
            Artifacts = _validator.ValidateArtifacts(request.Artifacts),
            Unverified = _validator.CleanStrings(request.Unverified),
            CreatedAt = now
        };
        FormationStateMachine.CollectRuntimeResult(formation, request.TaskId, submission);
    }

    private void PrepareRuntimeDispatch(
        FormationData formation,
        PrepareTaskDispatchRequest request)
    {
        _validator.ValidateIdentifier(request.TaskId, "taskId");
        _validator.ValidateChoice(
            request.DispatchMode,
            new[] { DispatchModes.Native, DispatchModes.Session },
            "dispatchMode");
        if (string.Equals(request.DispatchMode, DispatchModes.Session, StringComparison.Ordinal)
            && !request.UserAuthorized)
        {
            throw Conflict(
                "创建独立 Session 前必须获得用户明确授权。",
                "USER_AUTHORIZATION_REQUIRED",
                "userAuthorized");
        }

        var actorId = _validator.RequireActorId(request.ActorId);
        var task = FormationStateMachine.RequireTask(formation, request.TaskId);
        var sessionAuthorizationId = string.Empty;
        if (string.Equals(request.DispatchMode, DispatchModes.Session, StringComparison.Ordinal))
        {
            var quote = _validator.RequireText(
                request.AuthorizationQuote,
                "authorizationQuote");
            var scope = string.IsNullOrWhiteSpace(request.AuthorizationScope)
                ? task.Id
                : request.AuthorizationScope.Trim();
            var authorization = new FormationSessionAuthorizationData
            {
                Id = FormationRepository.NextId("session-auth"),
                RequestId = request.RequestId,
                AuthorizedBy = actorId,
                GrantedAt = DateTimeOffset.UtcNow,
                Scope = scope,
                SourceQuote = quote
            };
            formation.SessionAuthorizations.Add(authorization);
            sessionAuthorizationId = authorization.Id;
        }

        EnsureDispatchBudget(formation, request.DispatchMode);
        FormationStateMachine.PrepareRuntimeDispatch(
            formation,
            request.TaskId,
            request.DispatchMode,
            request.RequestId,
            actorId,
            sessionAuthorizationId,
            Math.Max(1, formation.Budget.MaxDepth - 1));
    }

    private void BindRuntime(
        FormationData formation,
        BindTaskRuntimeRequest request)
    {
        _validator.ValidateIdentifier(request.TaskId, "taskId");
        var attestation = ValidateHostAttestation(
            request.HostAttestation,
            request.BindingEvidence);
        FormationStateMachine.BindRuntime(
            formation,
            request.TaskId,
            request.AgentId,
            request.SessionId,
            attestation);
    }

    private FormationHostAttestationData ValidateHostAttestation(
        FormationHostAttestationData? attestation,
        string? legacyEvidence)
    {
        if (attestation is null)
        {
            throw Invalid("hostAttestation 不能为空。", "hostAttestation");
        }

        if (attestation.CapturedAt == default)
        {
            throw Invalid("hostAttestation.capturedAt 不能为空。", "hostAttestation.capturedAt");
        }

        return new FormationHostAttestationData
        {
            HostId = _validator.RequireText(attestation.HostId, "hostAttestation.hostId"),
            ToolCallId = _validator.RequireText(
                attestation.ToolCallId,
                "hostAttestation.toolCallId"),
            CapturedAt = attestation.CapturedAt,
            RawResultSha256 = _validator.ValidateSha256(
                attestation.RawResultSha256,
                "hostAttestation.rawResultSha256"),
            Evidence = string.IsNullOrWhiteSpace(attestation.Evidence)
                ? _validator.RequireText(legacyEvidence, "bindingEvidence")
                : attestation.Evidence.Trim()
        };
    }

    private FormationHostResultReferenceData ValidateHostResult(
        FormationHostResultReferenceData? hostResult)
    {
        if (hostResult is null)
        {
            throw Invalid("hostResult 不能为空。", "hostResult");
        }

        if (hostResult.CollectedAt == default)
        {
            throw Invalid("hostResult.collectedAt 不能为空。", "hostResult.collectedAt");
        }

        return new FormationHostResultReferenceData
        {
            HostId = _validator.RequireText(hostResult.HostId, "hostResult.hostId"),
            ThreadId = _validator.RequireText(hostResult.ThreadId, "hostResult.threadId"),
            TurnId = hostResult.TurnId?.Trim() ?? string.Empty,
            MessageId = hostResult.MessageId?.Trim() ?? string.Empty,
            CollectedAt = hostResult.CollectedAt,
            ContentSha256 = _validator.ValidateSha256(
                hostResult.ContentSha256,
                "hostResult.contentSha256"),
            AttestedBy = _validator.RequireText(
                hostResult.AttestedBy,
                "hostResult.attestedBy")
        };
    }

    private static void EnsureDispatchBudget(
        FormationData formation,
        string dispatchMode)
    {
        var activeCount = formation.Tasks
            .SelectMany(task => task.Attempts)
            .Count(attempt =>
                attempt.CompletedAt is null
                && string.Equals(
                    attempt.DispatchMode,
                    dispatchMode,
                    StringComparison.Ordinal));
        var limit = string.Equals(dispatchMode, DispatchModes.Session, StringComparison.Ordinal)
            ? Math.Min(formation.Budget.SessionMax, formation.Budget.TreeSessionMax)
            : formation.Budget.SubagentMax;
        if (activeCount >= limit)
        {
            throw Conflict(
                dispatchMode + " 派发预算已耗尽。",
                activeCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/"
                    + limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "budget");
        }
    }

    private FormationCommandData BuildSpawnRequest(
        FormationData formation,
        PrepareTaskDispatchRequest request)
    {
        var task = FormationStateMachine.RequireTask(formation, request.TaskId);
        var seat = FormationStateMachine.RequireSeat(
            formation,
            task.ExecutorSeatId,
            SeatRoles.Executor);
        var attempt = task.Attempts.LastOrDefault(item =>
            string.Equals(
                item.RequestId,
                request.RequestId,
                StringComparison.Ordinal))
            ?? throw Conflict(
                "找不到本次派发对应的 Attempt。",
                request.RequestId,
                "requestId");
        var profile = !string.IsNullOrWhiteSpace(request.AgentProfile)
            ? request.AgentProfile
            : !string.IsNullOrWhiteSpace(seat.AgentProfile)
                ? seat.AgentProfile
                : string.Equals(request.DispatchMode, DispatchModes.Session, StringComparison.Ordinal)
                    ? "bdtool-session-worker"
                    : "bdtool-implementer";
        var command = new FormationCommandData
        {
            Formation = _repository.CloneForOutput(formation, includeReceipts: false),
            RequestId = request.RequestId,
            Replayed = false,
            SpawnRequest = new FormationSpawnRequestData
            {
                CanSpawn = true,
                LeafOnly = true,
                UserAuthorizationRequired = string.Equals(
                    request.DispatchMode,
                    DispatchModes.Session,
                    StringComparison.Ordinal),
                SessionAuthorizationId = attempt.SessionAuthorizationId,
                DispatchMode = request.DispatchMode,
                FormationId = formation.Id,
                TaskId = task.Id,
                AttemptId = attempt.Id,
                RequestId = request.RequestId,
                RequirementVersion = formation.RequirementVersion,
                AgentProfile = profile,
                Model = string.IsNullOrWhiteSpace(request.Model) ? seat.Model : request.Model,
                Effort = string.IsNullOrWhiteSpace(request.Effort) ? seat.Effort : request.Effort,
                Prompt = BuildSpawnPrompt(formation, task, profile),
                Workspace = new FormationWorkspaceData
                {
                    Access = task.Workspace.Access,
                    WritePaths = new List<string>(task.Workspace.WritePaths)
                },
                Acceptance = task.Acceptance,
                MaxSubagents = attempt.EffectiveSubagentMax,
                MaxDepth = attempt.EffectiveMaxDepth,
                TimeoutMinutes = formation.Budget.TimeoutMinutes
            }
        };
        return command;
    }

    private static string BuildSpawnPrompt(
        FormationData formation,
        FormationTaskData task,
        string profile)
    {
        var paths = task.Workspace.WritePaths.Count == 0
            ? "(只读)"
            : string.Join(", ", task.Workspace.WritePaths);
        return "你是 BDTool Formation 的一次性叶子执行者，禁止创建子智能体、创建线程或继续委派。\n"
            + "Formation: " + formation.Id + "\n"
            + "Task: " + task.Id + " - " + task.Title + "\n"
            + "Profile: " + profile + "\n"
            + "目标: " + task.Objective + "\n"
            + "指令: " + task.Instructions + "\n"
            + "验收: " + task.Acceptance + "\n"
            + "访问: " + task.Workspace.Access + "\n"
            + "允许写入: " + paths + "\n"
            + "只能修改允许范围；返回实际变更、证据、未验证项，不得伪造测试或完成状态。";
    }

    private void ReviewTask(
        FormationData formation,
        ReviewFormationTaskRequest request)
    {
        _validator.ValidateIdentifier(request.TaskId, "taskId");
        var actorId = _validator.RequireActorId(request.ActorId);
        var task = FormationStateMachine.RequireTask(formation, request.TaskId);
        var reviewerSeatId = string.IsNullOrWhiteSpace(request.ReviewerSeatId)
            ? task.ReviewerSeatId
            : request.ReviewerSeatId;
        _validator.ValidateIdentifier(reviewerSeatId, "reviewerSeatId");
        FormationStateMachine.RequireSeat(formation, reviewerSeatId, SeatRoles.Reviewer);

        var now = DateTimeOffset.UtcNow;
        var review = new FormationReviewData
        {
            Id = FormationRepository.NextId("review"),
            SubmissionId = request.SubmissionId,
            RequestId = request.RequestId,
            ActorKind = FormationActorKinds.Human,
            ActorId = actorId,
            ReviewerSeatId = reviewerSeatId,
            Passed = request.Passed,
            Issues = _validator.ValidateIssues(request.Issues),
            MissingEvidence = _validator.CleanStrings(request.MissingEvidence),
            Feedback = request.Feedback?.Trim() ?? string.Empty,
            ReworkInstruction = request.ReworkInstruction?.Trim() ?? string.Empty,
            CreatedAt = now
        };
        FormationStateMachine.Review(formation, task, review);
    }

    private static string BuildEntityId(
        string formationId,
        string kind,
        string id) =>
        formationId + ":" + kind + ":" + id;

    private OperationResult<FormationCommandData> SuccessCommand(
        FormationData formation,
        string requestId,
        bool replayed,
        Func<FormationData, FormationCommandData>? commandFactory = null)
    {
        var data = commandFactory?.Invoke(formation)
            ?? new FormationCommandData
            {
                Formation = _repository.CloneForOutput(formation, includeReceipts: false)
            };
        data.RequestId = requestId;
        data.Replayed = replayed;
        return Success(data);
    }

    private OperationResult<FormationCommandData> FailureCommand(
        FormationData formation,
        string requestId,
        string code,
        string message,
        OperationStatus status,
        IReadOnlyDictionary<string, string>? details)
    {
        var result = new OperationResult<FormationCommandData>
        {
            Status = status,
            Data = new FormationCommandData
            {
                RequestId = requestId,
                Formation = _repository.CloneForOutput(formation, includeReceipts: false),
                Replayed = false
            }
        };
        result.Errors.Add(new KernelError
        {
            Code = code,
            Stage = "formation",
            Message = message,
            Retryable = false,
            Details = details is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(details, StringComparer.Ordinal)
        });
        return result;
    }

    private static FormationSummary ToSummary(FormationData formation) => new()
    {
        Id = formation.Id,
        Objective = formation.Objective,
        Status = formation.Status,
        RequirementVersion = formation.RequirementVersion,
        SeatCount = formation.Seats.Count,
        TaskCount = formation.Tasks.Count,
        PassedTaskCount = formation.Tasks.Count(task =>
            string.Equals(task.State, FormationTaskStates.Passed, StringComparison.Ordinal)),
        CreatedAt = formation.CreatedAt,
        UpdatedAt = formation.UpdatedAt
    };

    private static OperationResult<T> Success<T>(T data) => new()
    {
        Status = OperationStatus.Completed,
        Data = data
    };

    private static OperationResult<T> Failure<T>(
        string code,
        string message,
        OperationStatus status = OperationStatus.Failed,
        IReadOnlyDictionary<string, string>? details = null,
        bool retryable = false)
    {
        var result = new OperationResult<T> { Status = status };
        result.Errors.Add(new KernelError
        {
            Code = code,
            Stage = "formation",
            Message = message,
            Retryable = retryable,
            Details = details is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(details, StringComparer.Ordinal)
        });
        return result;
    }

    private static FormationServiceException Invalid(string message, string field) =>
        new(
            ErrorCodes.InvalidArgument,
            message,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["field"] = field
            });

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

    private static FormationServiceException NotFoundFormation(string formationId) =>
        new(
            ErrorCodes.NotFound,
            "Formation 不存在。",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["formationId"] = formationId
            });
}
