using System;
using System.Collections.Generic;
using System.Text.Json;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;

namespace BDTool.Provider.Official;

/// <summary>官方 Pipeline 的实例描述。EvalToken 只在内存中用于请求认证，不进入公共输出。</summary>
internal sealed class OfficialPipelineDescriptor
{
    public int Pid { get; set; }
    public int Port { get; set; }
    public string ProjectPath { get; set; } = string.Empty;
    public string UnityVersion { get; set; } = string.Empty;
    public DateTimeOffset LastHeartbeat { get; set; }
    public string EvalToken { get; set; } = string.Empty;
}

/// <summary>从官方 HTTP 状态端点收敛出的稳定状态快照。</summary>
internal sealed class OfficialPipelineStatusSnapshot
{
    public string State { get; set; } = "unknown";
    public string ProjectPath { get; set; } = string.Empty;
    public string UnityVersion { get; set; } = string.Empty;
    public int? ProcessId { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>官方 Pipeline 请求失败。错误码始终映射到公共 Editor 错误词表。</summary>
internal sealed class OfficialPipelineException : Exception
{
    public OfficialPipelineException(
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details = null)
        : base(message)
    {
        Code = code;
        Details = details ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public string Code { get; }
    public IReadOnlyDictionary<string, string> Details { get; }
}

/// <summary>把官方 HTTP/命令错误转换为 BDTool 稳定错误码，不泄漏认证令牌。</summary>
internal static class OfficialErrorMapper
{
    public static OfficialPipelineException FromResponse(
        int httpStatus,
        JsonElement? response,
        string stage)
    {
        var upstreamCode = response is { ValueKind: JsonValueKind.Object }
            && response.Value.TryGetProperty("errorCode", out var codeElement)
            && codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString() ?? string.Empty
                : string.Empty;
        var message = response is { ValueKind: JsonValueKind.Object }
            && response.Value.TryGetProperty("error", out var errorElement)
            && errorElement.ValueKind == JsonValueKind.String
                ? errorElement.GetString() ?? string.Empty
                : string.Empty;

        var publicCode = httpStatus switch
        {
            401 => ErrorCodes.EditorDisconnected,
            404 => ErrorCodes.EditorNotFound,
            _ => upstreamCode switch
            {
                "INVALID_COMMAND_ARGS" => ErrorCodes.InvalidArgument,
                "TIMEOUT" => ErrorCodes.Timeout,
                "BUSY" or "NOT_READY" => ErrorCodes.EditorNotReady,
                "COMMAND_FAILED" or "COMPILATION_ERROR" or "RUNTIME_EXCEPTION" =>
                    ErrorCodes.ExecutionFailed,
                _ => ErrorCodes.ExecutionFailed
            }
        };

        return new OfficialPipelineException(
            publicCode,
            string.IsNullOrWhiteSpace(message)
                ? "Unity Pipeline 请求失败（HTTP " + httpStatus + "）。"
                : message,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stage"] = stage,
                ["httpStatus"] = httpStatus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["upstreamCode"] = upstreamCode
            });
    }

    public static EditorException ToEditorException(
        OfficialPipelineException exception,
        string stage)
    {
        var details = new Dictionary<string, string>(exception.Details, StringComparer.Ordinal)
        {
            ["stage"] = stage
        };
        return new EditorException(exception.Code, exception.Message, details);
    }
}
