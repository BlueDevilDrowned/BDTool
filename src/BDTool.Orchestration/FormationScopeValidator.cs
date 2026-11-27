using BDTool.Contracts.Errors;
using BDTool.Contracts.Formation;
using BDTool.Core.Workspace;

namespace BDTool.Orchestration;

/// <summary>
/// Formation 输入、项目路径和写入范围校验。所有路径在进入状态文件前统一转换
/// 为 POSIX 项目相对路径，并沿用 Local Workspace 的保留名、ADS 与 reparse point 规则。
/// </summary>
internal sealed class FormationScopeValidator
{
    private readonly string _projectRoot;

    public FormationScopeValidator(string projectRoot)
    {
        _projectRoot = PathPolicy.NormalizeProjectRoot(projectRoot);
    }

    public void ValidateIdentifier(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw Invalid(field + " 必须是 1..128 位安全标识符。", field);
        }

        if (!char.IsLetterOrDigit(value[0])
            || value.Any(character =>
                !char.IsLetterOrDigit(character)
                && character is not '-' and not '_' and not '.'))
        {
            throw Invalid(field + " 只能包含字母、数字、点、下划线和连字符。", field);
        }

        try
        {
            var normalized = PathPolicy.NormalizeRelativePath(value);
            if (normalized.Contains("/", StringComparison.Ordinal))
            {
                throw Invalid(field + " 不能包含目录分隔符。", field);
            }
        }
        catch (PathPolicyException exception)
        {
            throw Invalid(field + " 不是安全标识符：" + exception.Message, field);
        }
    }

    public void ValidateRequestId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            throw Invalid("requestId 必须是 1..256 位非空字符串。", "requestId");
        }
    }

    public string RequireText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid(field + " 不能为空。", field);
        }

        return value.Trim();
    }

    public string RequireActorId(string? value)
    {
        var actorId = RequireText(value, "actorId");
        if (actorId.Length > 256
            || actorId.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            throw Invalid("actorId 必须是 1..256 位安全字符串。", "actorId");
        }

        return actorId;
    }

    public string ValidateSha256(string? value, string field)
    {
        var sha256 = RequireText(value, field);
        if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw Invalid(field + " 必须是 64 位十六进制 SHA-256。", field);
        }

        return sha256.ToLowerInvariant();
    }

    public void ValidateChoice(
        string value,
        IReadOnlyList<string> allowed,
        string field)
    {
        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            throw Invalid(field + " 取值无效。", field);
        }
    }

    public FormationBudgetData ValidateBudget(FormationBudgetData budget)
    {
        if (budget.SubagentMax < 0
            || budget.SessionMax < 0
            || budget.CrossAgentMax < 0
            || budget.TreeSessionMax < 0
            || budget.MaxDepth < 1
            || budget.TimeoutMinutes < 1
            || budget.ReviewRetryMax < 0)
        {
            throw Invalid("Formation budget 包含非法值。", "budget");
        }

        return new FormationBudgetData
        {
            SubagentMax = budget.SubagentMax,
            SessionMax = budget.SessionMax,
            CrossAgentMax = budget.CrossAgentMax,
            TreeSessionMax = budget.TreeSessionMax,
            MaxDepth = budget.MaxDepth,
            TimeoutMinutes = budget.TimeoutMinutes,
            ReviewRetryMax = budget.ReviewRetryMax
        };
    }

    public List<string> NormalizeWorkspace(
        string access,
        List<string>? paths,
        string field)
    {
        ValidateChoice(access, WorkspaceAccesses.All, "access");
        var normalized = (paths ?? new List<string>())
            .Select(path => NormalizeScopePath(path, field, allowStatePath: false))
            .ToList();
        normalized.Sort(StringComparer.OrdinalIgnoreCase);

        if (string.Equals(access, WorkspaceAccesses.Read, StringComparison.Ordinal))
        {
            if (normalized.Count > 0)
            {
                throw Invalid(field + " 在 read 访问下不能声明 writePaths。", "writePaths");
            }
        }
        else if (normalized.Count == 0)
        {
            throw Invalid(field + " 的 write 访问必须声明 writePaths。", "writePaths");
        }

        for (var index = 1; index < normalized.Count; index++)
        {
            if (PathsOverlap(normalized[index - 1], normalized[index]))
            {
                throw Invalid(field + " 的 writePaths 不能重叠。", "writePaths");
            }
        }

        return normalized;
    }

    public List<string> NormalizeChangedPaths(
        FormationTaskData task,
        List<string>? paths)
    {
        var changed = CleanStrings(paths)
            .Select(path => NormalizeScopePath(path, "changedPaths", allowStatePath: false))
            .ToList();
        if (string.Equals(task.Workspace.Access, WorkspaceAccesses.Read, StringComparison.Ordinal))
        {
            if (changed.Count > 0)
            {
                throw Invalid("只读任务不能报告 changedPaths。", "changedPaths");
            }

            return changed;
        }

        foreach (var path in changed)
        {
            if (!task.Workspace.WritePaths.Any(scope => IsWithinScope(path, scope)))
            {
                throw Conflict("changedPath 超出任务写入范围。", path, "changedPaths");
            }
        }

        return changed;
    }

    public List<FormationArtifactData> ValidateArtifacts(
        List<FormationArtifactData>? artifacts)
    {
        var result = new List<FormationArtifactData>();
        foreach (var artifact in artifacts ?? new List<FormationArtifactData>())
        {
            result.Add(new FormationArtifactData
            {
                Kind = RequireText(artifact.Kind, "artifact.kind"),
                Path = NormalizeScopePath(
                    RequireText(artifact.Path, "artifact.path"),
                    "artifact.path",
                    allowStatePath: true),
                Sha256 = ValidateSha256(artifact.Sha256, "artifact.sha256")
            });
        }

        return result;
    }

    public List<FormationReviewIssueData> ValidateIssues(
        List<FormationReviewIssueData>? issues)
    {
        var result = new List<FormationReviewIssueData>();
        foreach (var issue in issues ?? new List<FormationReviewIssueData>())
        {
            ValidateChoice(issue.Severity, new[]
            {
                ReviewSeverities.Error,
                ReviewSeverities.Warning,
                ReviewSeverities.Information
            }, "severity");
            result.Add(new FormationReviewIssueData
            {
                Severity = issue.Severity,
                Message = RequireText(issue.Message, "issue.message"),
                Evidence = issue.Evidence ?? string.Empty
            });
        }

        return result;
    }

    public List<string> CleanStrings(List<string>? values) =>
        (values ?? new List<string>())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .ToList();

    public void EnsureWritePathsWithinSeat(
        FormationSeatData seat,
        string access,
        List<string> writePaths)
    {
        if (string.Equals(access, WorkspaceAccesses.Read, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.Equals(seat.Workspace.Access, WorkspaceAccesses.Write, StringComparison.Ordinal))
        {
            throw Conflict("Executor 席位没有写访问。", seat.Id, "executorSeatId");
        }

        foreach (var path in writePaths)
        {
            if (!seat.Workspace.WritePaths.Any(scope => IsWithinScope(path, scope)))
            {
                throw Conflict("Task 写入范围超出 Executor 席位范围。", path, "writePaths");
            }
        }
    }

    public void EnsureNoSeatWriteOverlap(
        FormationData formation,
        string newSeatId,
        List<string> writePaths)
    {
        foreach (var seat in formation.Seats.Where(seat =>
                     string.Equals(seat.Workspace.Access, WorkspaceAccesses.Write, StringComparison.Ordinal)))
        {
            foreach (var path in writePaths)
            {
                if (seat.Workspace.WritePaths.Any(existing => PathsOverlap(existing, path)))
                {
                    throw Conflict("Executor 席位写入范围重叠。", newSeatId, "seatId");
                }
            }
        }
    }

    public void EnsureNoTaskWriteOverlap(
        FormationData formation,
        string newTaskId,
        string access,
        List<string> writePaths)
    {
        if (!string.Equals(access, WorkspaceAccesses.Write, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var task in formation.Tasks.Where(IsActiveWriteTask))
        {
            foreach (var path in writePaths)
            {
                if (task.Workspace.WritePaths.Any(existing => PathsOverlap(existing, path)))
                {
                    throw Conflict(
                        "Task 写入范围与其他活动任务重叠。",
                        newTaskId,
                        "taskId");
                }
            }
        }
    }

    private static bool IsActiveWriteTask(FormationTaskData task) =>
        string.Equals(task.Workspace.Access, WorkspaceAccesses.Write, StringComparison.Ordinal)
        && !string.Equals(task.State, FormationTaskStates.Passed, StringComparison.Ordinal)
        && !string.Equals(task.State, FormationTaskStates.Cancelled, StringComparison.Ordinal);

    private static bool IsWithinScope(string path, string scope) =>
        string.Equals(path, scope, PathPolicy.PathComparison)
        || path.StartsWith(scope + "/", PathPolicy.PathComparison);

    private static bool PathsOverlap(string left, string right) =>
        IsWithinScope(left, right) || IsWithinScope(right, left);

    private string NormalizeScopePath(string path, string field, bool allowStatePath)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            throw Invalid(field + " 包含空白或非法路径。", field);
        }

        string relative;
        try
        {
            if (Path.IsPathRooted(path))
            {
                var fullPath = Path.GetFullPath(path);
                if (!PathPolicy.IsWithinProjectRoot(_projectRoot, fullPath))
                {
                    throw Conflict("路径越出项目范围。", path, field);
                }

                relative = Path.GetRelativePath(_projectRoot, fullPath)
                    .Replace('\\', '/');
            }
            else
            {
                relative = PathPolicy.NormalizeRelativePath(path);
            }

            relative = PathPolicy.NormalizeRelativePath(relative);
            var isStatePath = string.Equals(relative, ".bdtool", PathPolicy.PathComparison)
                || relative.StartsWith(".bdtool/", PathPolicy.PathComparison);
            if (allowStatePath
                && !relative.StartsWith(".bdtool/artifacts/", PathPolicy.PathComparison))
            {
                if (isStatePath)
                {
                    throw Invalid(
                        field + " 只能引用 .bdtool/artifacts 下的证据文件。",
                        field);
                }
            }
            else if (!allowStatePath && isStatePath)
            {
                throw Invalid(field + " 不能指向 BDTool 内部状态目录。", field);
            }

            var absolutePath = PathPolicy.ResolveRelativePath(_projectRoot, relative);
            PathPolicy.EnsureNoReparsePoint(_projectRoot, absolutePath);
            return relative;
        }
        catch (PathPolicyException exception)
        {
            throw exception.Violation switch
            {
                PathPolicyViolation.InvalidPath =>
                    Invalid(field + " 路径不合法：" + exception.Message, field),
                _ => Conflict("路径不在安全项目范围内。", path, field)
            };
        }
    }

    private static FormationServiceException Invalid(string message, string field) =>
        new(
            ErrorCodes.InvalidArgument,
            message,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["field"] = field });

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
}
