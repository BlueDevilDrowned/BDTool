using System.Globalization;
using System.Text.Json;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;

namespace BDTool.Provider.Akio;

/// <summary>
/// unity-cli stdout 协议的唯一解析入口。stderr 只作为诊断计数，
/// 任何完整的人类日志都不会进入公共异常、Details 或 Provider 输出。
/// </summary>
internal static class AkioCliProtocol
{
    private const string EditorNotFoundCode = "EDITOR_NOT_FOUND";
    private const string EditorUnreachableCode = "EDITOR_UNREACHABLE";

    /// <summary>
    /// 校验退出码与输出信封，并返回仍由调用方负责释放的 JSON 文档。
    /// 只有 stdout 会被解析；stderr 即使是一段合法 JSON 也不会成为协议。
    /// </summary>
    public static JsonDocument ParseSuccess(UnityCliProcessResult result, string stage)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(stage))
        {
            throw new ArgumentException("stage 不能为空。", nameof(stage));
        }

        var payload = StripBom(result.Stdout);
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw CreateProtocolFailure(stage, result, "empty_stdout");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            throw CreateProtocolFailure(stage, result, "invalid_json");
        }

        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw CreateProtocolFailure(stage, result, "non_object_json");
            }

            var upstreamCode = FindUpstreamCode(root);
            if (result.ExitCode != 0)
            {
                throw CreateProtocolFailure(stage, result, "nonzero_exit", upstreamCode);
            }

            var failureReason = FindStructuredFailure(root);
            if (failureReason is not null)
            {
                throw CreateProtocolFailure(stage, result, failureReason, upstreamCode);
            }

            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 取出信封中的 data 原文。Provider 对业务层只暴露 data，
    /// 不把 command/success/warnings 等 CLI 外壳继续向上传递。
    /// </summary>
    public static string GetDataJson(JsonElement root, string stage)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data))
        {
            throw CreateProtocolFailure(stage, "missing_data");
        }

        return data.GetRawText();
    }

    /// <summary>
    /// 构造带脱敏进程诊断的协议失败。error 原文可能包含路径和人类日志，
    /// 因此这里只保留退出码、长度、stderr 是否存在以及上游稳定错误码。
    /// </summary>
    public static EditorException CreateProtocolFailure(
        string stage,
        UnityCliProcessResult result,
        string reason,
        string? upstreamCode = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var stderr = result.Stderr ?? string.Empty;
        var stdout = result.Stdout ?? string.Empty;
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stage"] = stage,
            ["reason"] = reason,
            ["exitCode"] = result.ExitCode.ToString(CultureInfo.InvariantCulture),
            ["stderrPresent"] = (!string.IsNullOrEmpty(stderr)).ToString(),
            ["stderrLength"] = stderr.Length.ToString(CultureInfo.InvariantCulture),
            ["stdoutLength"] = stdout.Length.ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(upstreamCode))
        {
            details["upstreamCode"] = upstreamCode;
        }

        var code = MapUpstreamCode(upstreamCode, result.ExitCode);
        return new EditorException(code, CreateMessage(code), details);
    }

    /// <summary>缺少协议字段时使用的本地结构失败，不伪造上游进程诊断。</summary>
    public static EditorException CreateProtocolFailure(string stage, string reason) =>
        new(
            ErrorCodes.ExecutionFailed,
            "unity-cli 返回的 JSON 协议缺少必要字段。",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stage"] = stage,
                ["reason"] = reason
            });

    private static string StripBom(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.StartsWith('\uFEFF') ? text[1..].TrimStart() : text;
    }

    private static string? FindStructuredFailure(JsonElement root)
    {
        if (IsFalse(root, "success"))
        {
            return "success_false";
        }

        if (!root.TryGetProperty("data", out var data))
        {
            return null;
        }

        if (data.ValueKind == JsonValueKind.Object)
        {
            if (IsFalse(data, "success"))
            {
                return "data_success_false";
            }

            if (string.Equals(GetString(data, "status"), "error", StringComparison.OrdinalIgnoreCase))
            {
                return "data_status_error";
            }

            if (data.TryGetProperty("error", out var error)
                && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return "data_error_present";
            }
        }

        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && IsFalse(item, "ok"))
                {
                    return "batch_item_failed";
                }
            }
        }

        return null;
    }

    private static string? FindUpstreamCode(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Object)
            {
                var directCode = GetString(data, "code");
                if (!string.IsNullOrWhiteSpace(directCode))
                {
                    return directCode;
                }

                if (data.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object)
                {
                    var nestedCode = GetString(error, "code");
                    if (!string.IsNullOrWhiteSpace(nestedCode))
                    {
                        return nestedCode;
                    }
                }
            }

            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && IsFalse(item, "ok"))
                    {
                        var batchCode = GetString(item, "code");
                        if (!string.IsNullOrWhiteSpace(batchCode))
                        {
                            return batchCode;
                        }
                    }
                }
            }
        }

        if (root.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in errors.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var errorCode = GetString(item, "code");
                if (!string.IsNullOrWhiteSpace(errorCode))
                {
                    return errorCode;
                }
            }
        }

        return GetString(root, "code");
    }

    private static string MapUpstreamCode(string? upstreamCode, int exitCode)
    {
        if (!string.IsNullOrWhiteSpace(upstreamCode))
        {
            return upstreamCode switch
            {
                "INVALID_ARGUMENT" => ErrorCodes.InvalidArgument,
                "PRECONDITION_FAILED" => ErrorCodes.PreconditionFailed,
                "TIMEOUT" => ErrorCodes.Timeout,
                EditorNotFoundCode => ErrorCodes.EditorNotFound,
                EditorUnreachableCode => ErrorCodes.EditorDisconnected,
                "EDITOR_NOT_READY" => ErrorCodes.EditorNotReady,
                _ => ErrorCodes.ExecutionFailed
            };
        }

        return exitCode switch
        {
            2 => ErrorCodes.InvalidArgument,
            4 => ErrorCodes.PreconditionFailed,
            7 => ErrorCodes.EditorDisconnected,
            _ => ErrorCodes.ExecutionFailed
        };
    }

    private static string CreateMessage(string code) => code switch
    {
        ErrorCodes.InvalidArgument => "unity-cli 拒绝了 Editor 请求参数。",
        ErrorCodes.PreconditionFailed => "unity-cli 前置条件校验失败。",
        ErrorCodes.Timeout => "unity-cli 操作在超时上限内未完成。",
        ErrorCodes.EditorNotFound => "未找到可连接的 Unity Editor。",
        ErrorCodes.EditorDisconnected => "无法连接项目绑定的 Unity Editor。",
        ErrorCodes.EditorNotReady => "Unity Editor 尚未就绪。",
        _ => "unity-cli 未返回成功协议。"
    };

    private static bool IsFalse(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.False;

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
