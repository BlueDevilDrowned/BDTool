using System;
using System.Collections.Generic;
using BDTool.Contracts.Errors;
using BDTool.Core.Workspace;

namespace BDTool.Provider.Local.Internal;

/// <summary>
/// 把 Provider 内部失败统一转换为带稳定错误码的 WorkspaceException。
/// 这一层不依赖 Application，因此细节可以被未来的 Handler 原样映射。
/// </summary>
internal static class WorkspaceErrors
{
    public static WorkspaceException Invalid(string message, params (string Key, string Value)[] details)
    {
        return new WorkspaceException(ErrorCodes.InvalidArgument, message, ToDetails(details));
    }

    public static WorkspaceException NotFound(string message, params (string Key, string Value)[] details)
    {
        return new WorkspaceException(ErrorCodes.NotFound, message, ToDetails(details));
    }

    public static WorkspaceException ProjectNotFound(string message, params (string Key, string Value)[] details)
    {
        return new WorkspaceException(ErrorCodes.ProjectNotFound, message, ToDetails(details));
    }

    public static WorkspaceException PreconditionFailed(
        string message,
        params (string Key, string Value)[] details)
    {
        return new WorkspaceException(ErrorCodes.PreconditionFailed, message, ToDetails(details));
    }

    public static WorkspaceException ExecutionFailed(string message, Exception innerException)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["exception"] = innerException.GetType().Name
        };

        return new WorkspaceException(ErrorCodes.ExecutionFailed, message, details, innerException);
    }

    public static WorkspaceException FromPathPolicy(
        PathPolicyException exception,
        string? projectPath,
        string? relativePath)
    {
        var details = new List<(string Key, string Value)>
        {
            ("violation", exception.Violation.ToString()),
            ("reason", ToReason(exception.Violation))
        };

        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            details.Add(("projectPath", projectPath));
        }

        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            details.Add(("relativePath", relativePath));
        }

        return Invalid(exception.Message, details.ToArray());
    }

    private static IReadOnlyDictionary<string, string> ToDetails(
        params (string Key, string Value)[] details)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var detail in details)
        {
            result[detail.Key] = detail.Value ?? string.Empty;
        }

        return result;
    }

    private static string ToReason(PathPolicyViolation violation)
    {
        return violation switch
        {
            PathPolicyViolation.OutsideProjectRoot => "outside-project-root",
            PathPolicyViolation.ReparsePoint => "reparse-point",
            _ => "invalid-path"
        };
    }
}
