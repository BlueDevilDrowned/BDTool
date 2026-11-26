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
using BDTool.Operations.Handlers;
using BDTool.Operations.Input;

namespace BDTool.Operations.Projects;

/// <summary>
/// project.context：返回可观察的离线项目事实。项目目录缺失返回 PROJECT_NOT_FOUND；
/// Unity 未连接不是失败，Editor 固定为 connected=false。Unity 版本只在
/// ProjectSettings/ProjectVersion.txt 可读时填写，不通过启动 Editor 推断。
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
                Message = "M1 不连接 Unity；Editor Provider 尚未实现。"
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
            data.Capabilities = await context.Capabilities.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            data.Capabilities = new Contracts.Capabilities.CapabilitySnapshot();
        }

        // M1 的 Editor 端口是显式不可用实现；只有未来真实 Provider 报告 ready 才可置为已连接。
        try
        {
            var status = await context.Editor.GetStatusAsync(
                new EditorStatusRequest { ProjectPath = data.ProjectPath },
                cancellationToken).ConfigureAwait(false);
            if (status is not null && string.Equals(status.State, "ready", StringComparison.Ordinal))
            {
                data.Editor.Connected = true;
                data.Editor.Status = status.State;
                data.Editor.Message = status.Message ?? string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            // 未实现 Editor 连接属于 M1 预期状态。
        }

        return new OperationResult<ProjectContextData>
        {
            Status = OperationStatus.Completed,
            Data = data
        };
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
