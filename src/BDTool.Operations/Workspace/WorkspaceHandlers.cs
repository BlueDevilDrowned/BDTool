using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Workspace;
using BDTool.Core.Workspace;
using BDTool.Operations.Handlers;
using BDTool.Operations.Input;

namespace BDTool.Operations.Workspace;

/// <summary>
/// workspace.read：把 JSON 输入解析为冻结的 WorkspaceReadRequest，并始终使用调用方
/// 显式项目路径覆盖输入中的 projectPath，防止一次请求跨越组合根绑定的项目。
/// </summary>
public sealed class WorkspaceReadHandler : IOperationHandler
{
    public const string StableOperationId = "workspace.read";

    public string OperationId => StableOperationId;

    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        IOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        WorkspaceReadRequest workspaceRequest;
        try
        {
            // projectPath 不属于操作输入：项目边界由组合根绑定的
            // OperationRequest.ProjectPath 决定，catalog 也不再声明该字段，
            // 避免出现“schema 声明了但 Handler 会忽略或拒绝”的漂移。
            using var input = OperationInputParser.ParseObject(
                request.InputJson,
                "relativePath");
            workspaceRequest = new WorkspaceReadRequest
            {
                ProjectPath = request.ProjectPath,
                RelativePath = OperationInputParser.RequireString(input.RootElement, "relativePath")
            };
        }
        catch (OperationInputException exception)
        {
            return OperationResults.Failure<WorkspaceReadResult>(
                ErrorCodes.InvalidArgument,
                "input",
                exception.Message);
        }

        try
        {
            var data = await context.Workspace.ReadAsync(workspaceRequest, cancellationToken).ConfigureAwait(false);
            return new OperationResult<WorkspaceReadResult>
            {
                Status = OperationStatus.Completed,
                Data = data
            };
        }
        catch (OperationCanceledException)
        {
            return Cancelled<WorkspaceReadResult>();
        }
        catch (WorkspaceException exception)
        {
            return OperationResults.FromWorkspaceException<WorkspaceReadResult>(exception);
        }
    }

    private static OperationResult<TData> Cancelled<TData>() where TData : new()
    {
        return OperationResults.Failure<TData>(
            ErrorCodes.Cancelled,
            "workspace",
            "调用已取消。",
            OperationStatus.Cancelled);
    }
}

/// <summary>workspace.search：字面量搜索，输入字段与冻结的 WorkspaceSearchRequest 对齐。</summary>
public sealed class WorkspaceSearchHandler : IOperationHandler
{
    public const string StableOperationId = "workspace.search";

    public string OperationId => StableOperationId;

    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        IOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        WorkspaceSearchRequest workspaceRequest;
        try
        {
            using var input = OperationInputParser.ParseObject(
                request.InputJson,
                "query",
                "glob",
                "maxResults");
            workspaceRequest = new WorkspaceSearchRequest
            {
                ProjectPath = request.ProjectPath,
                Query = OperationInputParser.RequireString(input.RootElement, "query"),
                Glob = OperationInputParser.GetString(input.RootElement, "glob") ?? "**/*",
                MaxResults = OperationInputParser.GetInt32(input.RootElement, "maxResults", 200)
            };
        }
        catch (OperationInputException exception)
        {
            return OperationResults.Failure<WorkspaceSearchResult>(
                ErrorCodes.InvalidArgument,
                "input",
                exception.Message);
        }

        try
        {
            var data = await context.Workspace.SearchAsync(workspaceRequest, cancellationToken).ConfigureAwait(false);
            return new OperationResult<WorkspaceSearchResult>
            {
                Status = OperationStatus.Completed,
                Data = data
            };
        }
        catch (OperationCanceledException)
        {
            return OperationResults.Failure<WorkspaceSearchResult>(
                ErrorCodes.Cancelled,
                "workspace",
                "调用已取消。",
                OperationStatus.Cancelled);
        }
        catch (WorkspaceException exception)
        {
            return OperationResults.FromWorkspaceException<WorkspaceSearchResult>(exception);
        }
    }
}

/// <summary>workspace.fingerprint：批量返回文件存在性与原始字节 SHA-256。</summary>
public sealed class WorkspaceFingerprintHandler : IOperationHandler
{
    public const string StableOperationId = "workspace.fingerprint";

    public string OperationId => StableOperationId;

    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        IOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        WorkspaceFingerprintRequest workspaceRequest;
        try
        {
            using var input = OperationInputParser.ParseObject(
                request.InputJson,
                "relativePaths");
            workspaceRequest = new WorkspaceFingerprintRequest
            {
                ProjectPath = request.ProjectPath,
                // relativePaths 是必填字段：缺失或 null 必须报 INVALID_ARGUMENT，
                // 只有显式空数组才表示"调用方确认本次没有要计算的文件"。
                RelativePaths = OperationInputParser.RequireStringArray(input.RootElement, "relativePaths")
            };
        }
        catch (OperationInputException exception)
        {
            return OperationResults.Failure<WorkspaceFingerprintResult>(
                ErrorCodes.InvalidArgument,
                "input",
                exception.Message);
        }

        try
        {
            var data = await context.Workspace.FingerprintAsync(workspaceRequest, cancellationToken).ConfigureAwait(false);
            return new OperationResult<WorkspaceFingerprintResult>
            {
                Status = OperationStatus.Completed,
                Data = data
            };
        }
        catch (OperationCanceledException)
        {
            return OperationResults.Failure<WorkspaceFingerprintResult>(
                ErrorCodes.Cancelled,
                "workspace",
                "调用已取消。",
                OperationStatus.Cancelled);
        }
        catch (WorkspaceException exception)
        {
            return OperationResults.FromWorkspaceException<WorkspaceFingerprintResult>(exception);
        }
    }
}

/// <summary>
/// workspace.apply：写入 UTF-8 文本。请求级 dryRun 与输入 dryRun 取逻辑或，
/// 因此 CLI 的 --dry-run 和 JSON 字段都能表达只预检。
/// </summary>
public sealed class WorkspaceApplyHandler : IOperationHandler
{
    public const string StableOperationId = "workspace.apply";

    public string OperationId => StableOperationId;

    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        IOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        WorkspaceChangeRequest workspaceRequest;
        try
        {
            using var input = OperationInputParser.ParseObject(
                request.InputJson,
                "relativePath",
                "content",
                "expectedSha256",
                "createIfMissing",
                "dryRun");
            workspaceRequest = new WorkspaceChangeRequest
            {
                ProjectPath = request.ProjectPath,
                RelativePath = OperationInputParser.RequireString(input.RootElement, "relativePath"),
                // content 缺失不能折叠成空字符串：那会把"漏传参数"变成"清空文件"。
                // 只有调用方显式提供 content="" 才表示有意清空。
                Content = OperationInputParser.RequireStringAllowEmpty(input.RootElement, "content"),
                ExpectedSha256 = OperationInputParser.GetNullableString(input.RootElement, "expectedSha256"),
                CreateIfMissing = OperationInputParser.GetBoolean(input.RootElement, "createIfMissing"),
                DryRun = request.DryRun || OperationInputParser.GetBoolean(input.RootElement, "dryRun")
            };
        }
        catch (OperationInputException exception)
        {
            return OperationResults.Failure<WorkspaceChangeResult>(
                ErrorCodes.InvalidArgument,
                "input",
                exception.Message);
        }

        try
        {
            var data = await context.Workspace.ApplyAsync(workspaceRequest, cancellationToken).ConfigureAwait(false);
            return new OperationResult<WorkspaceChangeResult>
            {
                Status = OperationStatus.Completed,
                Data = data
            };
        }
        catch (OperationCanceledException)
        {
            return OperationResults.Failure<WorkspaceChangeResult>(
                ErrorCodes.Cancelled,
                "workspace",
                "调用已取消。",
                OperationStatus.Cancelled);
        }
        catch (WorkspaceException exception)
        {
            return OperationResults.FromWorkspaceException<WorkspaceChangeResult>(exception);
        }
    }
}
