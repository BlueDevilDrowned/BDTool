using System;
using System.Collections.Generic;
using BDTool.Contracts;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Core.Workspace;

namespace BDTool.Operations.Handlers;

/// <summary>
/// Handler 共用的结果构造器。失败时仍保留泛型类型，保证协议中的 data 字段存在，
/// 同时把错误码、阶段与脱敏详情集中在同一处映射。
/// </summary>
internal static class OperationResults
{
    /// <summary>构造带 typed 空数据的失败结果。</summary>
    public static OperationResult<TData> Failure<TData>(
        string code,
        string stage,
        string message,
        OperationStatus status = OperationStatus.Failed,
        IReadOnlyDictionary<string, string>? details = null)
        where TData : new()
    {
        var result = new OperationResult<TData>
        {
            Status = status,
            Data = new TData()
        };
        result.Errors.Add(new KernelError
        {
            Code = code,
            Stage = stage,
            Message = message,
            Retryable = false,
            Details = details is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(details, StringComparer.Ordinal)
        });
        return result;
    }

    /// <summary>
    /// WorkspaceException 原样保留 Provider 的稳定错误码与详情；只有调用方取消映射为
    /// Cancelled，其余业务失败保持 Failed，避免被折叠成统一的执行失败。
    /// </summary>
    public static OperationResult<TData> FromWorkspaceException<TData>(WorkspaceException exception)
        where TData : new()
    {
        ArgumentNullException.ThrowIfNull(exception);
        var cancelled = string.Equals(
            exception.Code,
            ErrorCodes.Cancelled,
            StringComparison.Ordinal);
        return Failure<TData>(
            exception.Code,
            "workspace",
            exception.Message,
            cancelled ? OperationStatus.Cancelled : OperationStatus.Failed,
            exception.Details);
    }
}
