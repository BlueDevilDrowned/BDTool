using System.Globalization;
using System.Text.Json;
using BDTool.Composition;
using BDTool.Contracts.Formation;
using BDTool.Contracts.Operations;

namespace BDTool.Cli;

/// <summary>
/// formation 子命令解析结果。该入口直接调用 Formation Application Port，
/// 不进入 catalog/Gateway，因此人工编队 mutation 不会生成 Provider Activity。
/// </summary>
internal sealed class FormationInvocation
{
    public string Command { get; init; } = string.Empty;
    public string? PositionalId { get; init; }
    public FormationArguments Arguments { get; init; } = new();
    public CliOptions RuntimeOptions { get; init; } = new();
}

internal sealed class FormationParseResult
{
    public FormationInvocation? Invocation { get; init; }
    public string? Error { get; init; }
}

/// <summary>可重复选项与单值选项分开保存，避免 writePath/artifact 等列表被静默覆盖。</summary>
internal sealed class FormationArguments
{
    private readonly Dictionary<string, string> _values =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _multiValues =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags =
        new(StringComparer.Ordinal);

    public bool HasFlag(string name) => _flags.Contains(name);

    public string? GetOptional(string name) =>
        _values.TryGetValue(name, out var value) ? value : null;

    public string GetRequired(string name)
    {
        var value = GetOptional(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormationCliParseException("缺少 --" + name + "。");
        }

        return value;
    }

    public int? GetOptionalInt(string name)
    {
        var value = GetOptional(name);
        if (value is null)
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new FormationCliParseException("--" + name + " 必须是整数。");
        }

        return parsed;
    }

    public List<string> GetMany(string name) =>
        _multiValues.TryGetValue(name, out var values)
            ? new List<string>(values)
            : new List<string>();

    public void AddFlag(string name) => _flags.Add(name);

    public void AddValue(string name, string value)
    {
        if (_values.ContainsKey(name))
        {
            throw new FormationCliParseException("重复选项 --" + name + "。");
        }

        _values[name] = value;
    }

    public void AddMultiValue(string name, string value)
    {
        if (!_multiValues.TryGetValue(name, out var values))
        {
            values = new List<string>();
            _multiValues[name] = values;
        }

        values.Add(value);
    }
}

internal sealed class FormationCliParseException : Exception
{
    public FormationCliParseException(string message)
        : base(message)
    {
    }
}

internal static class FormationCli
{
    private static readonly HashSet<string> s_flagOptions = new(StringComparer.Ordinal)
    {
        "human", "json", "include-receipts", "passed", "failed"
    };

    private static readonly HashSet<string> s_multiOptions = new(StringComparer.Ordinal)
    {
        "write-path", "changed-path", "artifact-json", "issue-json",
        "missing-evidence", "unverified"
    };

    private static readonly HashSet<string> s_singleOptions = new(StringComparer.Ordinal)
    {
        "project", "catalog", "unity-cli", "unity-editor", "editor-provider",
        "id", "objective", "request-id", "budget-json", "formation", "task",
        "role", "display-name", "agent-profile", "model", "effort", "access",
        "subagent-max", "title", "instructions", "acceptance", "executor-seat",
        "reviewer-seat", "review-retry-max", "summary", "submission", "feedback",
        "rework-instruction", "actor-id"
    };

    public static FormationParseResult Parse(string[] args)
    {
        try
        {
            if (args.Length < 2 || args[1].StartsWith('-'))
            {
                throw new FormationCliParseException(
                    "formation 需要 create、seat、task、cancel、archive、show 或 list 子命令。");
            }

            var command = args[1];
            string? positionalId = null;
            var optionStart = 2;
            switch (command)
            {
                case "seat":
                case "task":
                    if (args.Length < 3 || args[2].StartsWith('-'))
                    {
                        throw new FormationCliParseException(
                            "formation " + command + " 需要一个操作子命令。");
                    }

                    command += "." + args[2];
                    optionStart = 3;
                    break;
                case "show":
                    if (args.Length > 2 && !args[2].StartsWith('-'))
                    {
                        positionalId = args[2];
                        optionStart = 3;
                    }

                    break;
            }

            var arguments = new FormationArguments();
            for (var index = optionStart; index < args.Length; index++)
            {
                var option = args[index];
                if (!option.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new FormationCliParseException("无法识别的位置参数 " + option + "。");
                }

                var name = option.Substring(2);
                if (s_flagOptions.Contains(name))
                {
                    arguments.AddFlag(name);
                    continue;
                }

                if (index + 1 >= args.Length)
                {
                    throw new FormationCliParseException("--" + name + " 需要一个值。");
                }

                var value = args[++index];
                if (value.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new FormationCliParseException("--" + name + " 需要一个值。");
                }

                if (s_multiOptions.Contains(name))
                {
                    arguments.AddMultiValue(name, value);
                    continue;
                }

                if (!s_singleOptions.Contains(name))
                {
                    throw new FormationCliParseException("未知选项 --" + name + "。");
                }

                if (string.Equals(name, "request-id", StringComparison.Ordinal)
                    && string.Equals(value, "auto", StringComparison.Ordinal))
                {
                    value = Guid.NewGuid().ToString("n");
                }

                arguments.AddValue(name, value);
            }

            if (arguments.HasFlag("human") && arguments.HasFlag("json"))
            {
                throw new FormationCliParseException("--human 与 --json 不能同时使用。");
            }

            return new FormationParseResult
            {
                Invocation = new FormationInvocation
                {
                    Command = command,
                    PositionalId = positionalId,
                    Arguments = arguments,
                    RuntimeOptions = new CliOptions
                    {
                        Human = arguments.HasFlag("human"),
                        Json = !arguments.HasFlag("human") || arguments.HasFlag("json"),
                        ProjectPath = arguments.GetOptional("project") ?? string.Empty,
                        CatalogPath = arguments.GetOptional("catalog"),
                        UnityCliPath = arguments.GetOptional("unity-cli"),
                        UnityEditorPath = arguments.GetOptional("unity-editor"),
                        EditorProvider = arguments.GetOptional("editor-provider") ?? "akio"
                    }
                }
            };
        }
        catch (FormationCliParseException exception)
        {
            return new FormationParseResult { Error = exception.Message };
        }
    }

    public static async Task<OperationResult> ExecuteAsync(
        FormationInvocation invocation,
        BDToolRuntime runtime,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (invocation.Command)
            {
                case "create":
                    return await runtime.Formation.CreateFormationAsync(
                        BuildCreate(invocation.Arguments),
                        cancellationToken).ConfigureAwait(false);
                case "seat.add":
                    return await runtime.Formation.AddSeatAsync(
                        BuildAddSeat(invocation.Arguments),
                        cancellationToken).ConfigureAwait(false);
                case "task.create":
                    return await runtime.Formation.CreateTaskAsync(
                        BuildCreateTask(invocation.Arguments),
                        cancellationToken).ConfigureAwait(false);
                case "task.dispatch":
                    return await runtime.Formation.DispatchTaskAsync(
                        BuildTaskCommand(invocation.Arguments),
                        cancellationToken).ConfigureAwait(false);
                case "task.start":
                    return await runtime.Formation.StartManualTaskAsync(
                        BuildTaskCommand(invocation.Arguments),
                        cancellationToken).ConfigureAwait(false);
                case "task.submit":
                    return await runtime.Formation.SubmitTaskAsync(
                        BuildSubmit(invocation.Arguments),
                        cancellationToken).ConfigureAwait(false);
                case "task.review":
                    return await runtime.Formation.ReviewTaskAsync(
                        BuildReview(invocation.Arguments),
                        cancellationToken).ConfigureAwait(false);
                case "task.rework":
                    return await runtime.Formation.PrepareReworkAsync(
                        BuildTaskCommand(invocation.Arguments),
                        cancellationToken).ConfigureAwait(false);
                case "task.cancel":
                    return await runtime.Formation.CancelTaskAsync(
                        BuildTaskCommand(invocation.Arguments),
                        cancellationToken).ConfigureAwait(false);
                case "cancel":
                    return await runtime.Formation.CancelFormationAsync(
                        BuildFormationCommand(invocation.Arguments),
                        cancellationToken).ConfigureAwait(false);
                case "archive":
                    return await runtime.Formation.ArchiveFormationAsync(
                        BuildFormationCommand(invocation.Arguments),
                        cancellationToken).ConfigureAwait(false);
                case "show":
                    var formationId = invocation.PositionalId
                        ?? invocation.Arguments.GetRequired("id");
                    return await runtime.Formation.GetFormationAsync(
                        formationId,
                        invocation.Arguments.HasFlag("include-receipts"),
                        cancellationToken).ConfigureAwait(false);
                case "list":
                    return await runtime.Formation.ListFormationsAsync(cancellationToken)
                        .ConfigureAwait(false);
                default:
                    throw new FormationCliParseException(
                        "不支持的 formation 子命令 " + invocation.Command + "。");
            }
        }
        catch (FormationCliParseException exception)
        {
            var result = new OperationResult();
            result.Errors.Add(new BDTool.Contracts.Errors.KernelError
            {
                Code = BDTool.Contracts.Errors.ErrorCodes.InvalidArgument,
                Stage = "formation",
                Message = exception.Message,
                Retryable = false,
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
            });
            return result;
        }
    }

    private static CreateFormationRequest BuildCreate(FormationArguments arguments) => new()
    {
        FormationId = arguments.GetRequired("id"),
        Objective = arguments.GetRequired("objective"),
        RequestId = arguments.GetRequired("request-id"),
        Budget = ParseJson<FormationBudgetData>(arguments.GetOptional("budget-json"))
    };

    private static AddSeatRequest BuildAddSeat(FormationArguments arguments) => new()
    {
        FormationId = arguments.GetRequired("formation"),
        SeatId = arguments.GetRequired("id"),
        Role = ParseRole(arguments.GetOptional("role")),
        DisplayName = arguments.GetRequired("display-name"),
        AgentProfile = arguments.GetOptional("agent-profile") ?? string.Empty,
        Model = arguments.GetOptional("model") ?? string.Empty,
        Effort = arguments.GetOptional("effort") ?? string.Empty,
        Access = ResolveAccess(arguments),
        WritePaths = arguments.GetMany("write-path"),
        SubagentMax = arguments.GetOptionalInt("subagent-max") ?? 0,
        RequestId = arguments.GetRequired("request-id")
    };

    private static CreateFormationTaskRequest BuildCreateTask(FormationArguments arguments) => new()
    {
        FormationId = arguments.GetRequired("formation"),
        TaskId = arguments.GetRequired("id"),
        Title = arguments.GetRequired("title"),
        Objective = arguments.GetRequired("objective"),
        Instructions = arguments.GetRequired("instructions"),
        Acceptance = arguments.GetRequired("acceptance"),
        ExecutorSeatId = arguments.GetRequired("executor-seat"),
        ReviewerSeatId = arguments.GetRequired("reviewer-seat"),
        Access = ResolveAccess(arguments),
        WritePaths = arguments.GetMany("write-path"),
        ReviewRetryMax = arguments.GetOptionalInt("review-retry-max") ?? 2,
        RequestId = arguments.GetRequired("request-id")
    };

    private static TaskCommandRequest BuildTaskCommand(FormationArguments arguments) => new()
    {
        FormationId = arguments.GetRequired("formation"),
        TaskId = arguments.GetRequired("task"),
        RequestId = arguments.GetRequired("request-id"),
        ActorId = arguments.GetOptional("actor-id") ?? "manual-user"
    };

    private static TaskCommandRequest BuildFormationCommand(FormationArguments arguments) => new()
    {
        FormationId = arguments.GetRequired("formation"),
        RequestId = arguments.GetRequired("request-id"),
        ActorId = arguments.GetOptional("actor-id") ?? "manual-user"
    };

    private static SubmitFormationTaskRequest BuildSubmit(FormationArguments arguments) => new()
    {
        FormationId = arguments.GetRequired("formation"),
        TaskId = arguments.GetRequired("task"),
        RequestId = arguments.GetRequired("request-id"),
        ActorId = arguments.GetOptional("actor-id") ?? "manual-user",
        Summary = arguments.GetRequired("summary"),
        ChangedPaths = arguments.GetMany("changed-path"),
        Artifacts = arguments.GetMany("artifact-json")
            .Select(value => ParseJson<FormationArtifactData>(value)
                ?? throw new FormationCliParseException("--artifact-json 不能为 null。"))
            .ToList(),
        Unverified = arguments.GetMany("unverified")
    };

    private static ReviewFormationTaskRequest BuildReview(FormationArguments arguments)
    {
        var passed = arguments.HasFlag("passed");
        var failed = arguments.HasFlag("failed");
        if (passed == failed)
        {
            throw new FormationCliParseException("review 必须且只能指定 --passed 或 --failed。");
        }

        return new ReviewFormationTaskRequest
        {
            FormationId = arguments.GetRequired("formation"),
            TaskId = arguments.GetRequired("task"),
            SubmissionId = arguments.GetRequired("submission"),
            ReviewerSeatId = arguments.GetOptional("reviewer-seat") ?? string.Empty,
            RequestId = arguments.GetRequired("request-id"),
            ActorId = arguments.GetOptional("actor-id") ?? "manual-user",
            Passed = passed,
            Issues = arguments.GetMany("issue-json")
                .Select(value => ParseJson<FormationReviewIssueData>(value)
                    ?? throw new FormationCliParseException("--issue-json 不能为 null。"))
                .ToList(),
            MissingEvidence = arguments.GetMany("missing-evidence"),
            Feedback = arguments.GetOptional("feedback") ?? string.Empty,
            ReworkInstruction = arguments.GetOptional("rework-instruction") ?? string.Empty
        };
    }

    /// <summary>
    /// CLI 的 --write-path 本身就是写意图；未显式指定 --access 时按 write 处理，
    /// 避免用户声明写入范围却被 read 默认值拒绝。显式 --access 仍以用户输入为准。
    /// </summary>
    private static string ResolveAccess(FormationArguments arguments) =>
        arguments.GetOptional("access")
        ?? (arguments.GetMany("write-path").Count > 0
            ? WorkspaceAccesses.Write
            : WorkspaceAccesses.Read);

    /// <summary>
    /// 角色常量是 PascalCase，CLI 允许大小写不敏感输入并归一化到契约常量；
    /// 未知角色在解析阶段即返回 INVALID_ARGUMENT，而不是静默回退。
    /// </summary>
    private static string ParseRole(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SeatRoles.Executor;
        }

        var match = SeatRoles.All.FirstOrDefault(role =>
            string.Equals(role, value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new FormationCliParseException(
                "--role 必须是 " + string.Join("、", SeatRoles.All) + "。");
        }

        return match;
    }
    private static T? ParseJson<T>(string? json)
    {
        if (json is null)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException exception)
        {
            throw new FormationCliParseException("JSON 参数无效：" + exception.Message);
        }
    }
}
