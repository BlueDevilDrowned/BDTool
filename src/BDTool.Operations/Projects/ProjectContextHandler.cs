using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Projects;
using BDTool.Contracts.Providers;
using BDTool.Core.Activity;
using BDTool.Core.Workspace;
using BDTool.Operations.Diagnostics;
using BDTool.Operations.Handlers;
using BDTool.Operations.Input;

namespace BDTool.Operations.Projects;

/// <summary>
/// project.context：返回项目事实与可用的 Editor Provider 状态。项目目录缺失返回
/// PROJECT_NOT_FOUND；Editor 未连接不是失败。Provider 异常只降级为未连接，
/// 不用推测状态覆盖真实失败。
/// </summary>
public sealed class ProjectContextHandler : IOperationHandler
{
    public const string StableOperationId = "project.context";

    private const string ProjectVersionRelativePath = "ProjectSettings/ProjectVersion.txt";

    public string OperationId => StableOperationId;

    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        IOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var input = OperationInputParser.ParseObject(request.InputJson);
        }
        catch (OperationInputException exception)
        {
            return OperationResults.Failure<ProjectContextData>(
                ErrorCodes.InvalidArgument,
                "input",
                exception.Message);
        }

        var projectPath = request.ProjectPath ?? string.Empty;
        if (!Directory.Exists(projectPath))
        {
            return OperationResults.Failure<ProjectContextData>(
                ErrorCodes.ProjectNotFound,
                "project",
                "项目目录不存在：" + projectPath + "。",
                details: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectPath"] = projectPath
                });
        }

        var unityVersion = await TryReadUnityVersionAsync(projectPath, cancellationToken).ConfigureAwait(false);
        var data = new ProjectContextData
        {
            ProjectPath = Path.GetFullPath(projectPath),
            ProjectExists = true,
            UnityVersion = unityVersion,
            Editor = new ProjectEditorContext
            {
                Connected = false,
                Status = "not-connected",
                Message = "当前未连接 Unity Editor。"
            },
            ActivityLimits = new ProjectActivityLimits
            {
                LargeContentThresholdBytes = ActivityPolicy.LargeContentThresholdBytes,
                RetentionDays = ActivityPolicy.DefaultRetentionDays
            }
        };

        try
        {
            data.Providers.Add(await context.Workspace.DescribeAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkspaceException exception)
        {
            return OperationResults.FromWorkspaceException<ProjectContextData>(exception);
        }
        catch (NotSupportedException)
        {
            // Workspace 未装配时仍允许查看项目事实，但不能伪造 Provider。
        }

        try
        {
            var editorProvider = await context.Editor.DescribeAsync(cancellationToken).ConfigureAwait(false);
            data.Providers.Add(editorProvider);
            data.Editor.ProviderId = editorProvider.Id;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            // 没有可用 Editor Provider 时仍返回离线项目上下文。
        }
        catch (EditorException exception)
        {
            // Provider 描述自身失败时仍返回项目事实，但 Editor 状态必须显式标成 error，
            // 不能继续显示 not-connected 让调用方误以为只是没启动编辑器。
            data.Editor.Connected = false;
            data.Editor.Status = "error";
            data.Editor.Message = exception.Code + ": " + exception.Message;
        }

        try
        {
            data.Capabilities = await context.Capabilities.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            data.Capabilities = new Contracts.Capabilities.CapabilitySnapshot();
        }

        // 只有真实 Provider 报告 ready 才可置为已连接；其他状态保留给调用方诊断。
        try
        {
            var status = await context.Editor.GetStatusAsync(
                new EditorStatusRequest { ProjectPath = data.ProjectPath },
                cancellationToken).ConfigureAwait(false);
            if (status is not null)
            {
                // ready 但项目不一致时不能算已连接，否则多开 Editor 会返回错误项目的事实。
                var projectMatches = EditorProjectMatch.Matches(
                    data.ProjectPath,
                    status.ProjectPath);
                data.Editor.Connected = string.Equals(status.State, "ready", StringComparison.Ordinal)
                    && projectMatches;
                data.Editor.Status = status.State;
                data.Editor.Message = BuildEditorMessage(status, data.ProjectPath, projectMatches);
                data.Editor.UnityVersion = string.IsNullOrWhiteSpace(status.UnityVersion)
                    ? data.UnityVersion
                    : status.UnityVersion;
                data.Editor.ProcessId = status.ProcessId;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            // 未装配 Editor Provider 时保持 not-connected。
        }
        catch (TimeoutException exception)
        {
            // 超时是稳定的可诊断状态；外层上下文查询仍成功，避免把项目事实一起丢掉。
            data.Editor.Connected = false;
            data.Editor.Status = "timeout";
            data.Editor.Message = "Editor 状态查询超时：" + exception.Message;
        }
        catch (EditorException exception)
        {
            data.Editor.Connected = false;
            data.Editor.Status = "error";
            data.Editor.Message = exception.Code + ": " + exception.Message;
        }

        return new OperationResult<ProjectContextData>
        {
            Status = OperationStatus.Completed,
            Data = data
        };
    }

    /// <summary>
    /// 状态消息优先保留 Provider 原话，并在项目不一致时明确原因；空路径表示
    /// Provider 无法证明连接归属，同样不能显示成“已连接当前项目”。
    /// </summary>
    private static string BuildEditorMessage(
        EditorStatus status,
        string requestedProjectPath,
        bool projectMatches)
    {
        if (projectMatches)
        {
            return status.Message ?? string.Empty;
        }

        var mismatch = string.IsNullOrWhiteSpace(status.ProjectPath)
            ? "Editor 未报告项目路径，无法确认连接属于当前项目。"
            : "Editor 报告的项目 " + status.ProjectPath + " 与请求项目 " + requestedProjectPath + " 不一致。";
        return string.IsNullOrWhiteSpace(status.Message)
            ? mismatch
            : mismatch + " " + status.Message;
    }

    /// <summary>
    /// 从 ProjectVersion.txt 的首个 m_EditorVersion 行读取版本。文件缺失、编码错误或
    /// 内容不符合 Unity 格式都保持 null，不阻断上下文查询。
    /// </summary>
    private static async Task<string?> TryReadUnityVersionAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        var versionPath = Path.Combine(
            projectPath,
            ProjectVersionRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(versionPath))
        {
            return null;
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(versionPath, cancellationToken).ConfigureAwait(false);
            foreach (var line in lines)
            {
                const string prefix = "m_EditorVersion:";
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var value = line.Substring(prefix.Length).Trim();
                    return value.Length == 0 ? null : value;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }
}
