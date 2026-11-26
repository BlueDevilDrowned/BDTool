using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace BDTool.Core.Workspace;

/// <summary>
/// Workspace 操作的稳定失败载体。公共错误码由 Provider 填写，Details 只保存已字符串化的
/// 诊断字段，便于 Application/CLI 在不依赖具体 Provider 类型的情况下映射协议错误。
/// </summary>
public sealed class WorkspaceException : Exception
{
    public WorkspaceException(string code, string message)
        : this(code, message, details: null, innerException: null)
    {
    }

    public WorkspaceException(string code, string message, Exception? innerException)
        : this(code, message, details: null, innerException)
    {
    }

    public WorkspaceException(
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Workspace 错误码不能为空。", nameof(code));
        }

        Code = code;

        // 复制调用方字典并包成只读视图，避免异常穿过异步边界后诊断字段仍被外部修改。
        var copiedDetails = new Dictionary<string, string>(StringComparer.Ordinal);
        if (details is not null)
        {
            foreach (var pair in details)
            {
                copiedDetails[pair.Key] = pair.Value ?? string.Empty;
            }
        }

        Details = new ReadOnlyDictionary<string, string>(copiedDetails);
    }

    /// <summary>稳定公共错误码，取值见 BDTool.Contracts.Errors.ErrorCodes。</summary>
    public string Code { get; }

    /// <summary>脱敏后的结构化诊断字段。</summary>
    public IReadOnlyDictionary<string, string> Details { get; }
}
