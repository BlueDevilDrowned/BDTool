using System.Text.Json;
using System.Text.Json.Serialization;
using BDTool.Contracts.Operations;

namespace BDTool.Cli;

/// <summary>
/// CLI 输出格式。--json 时 stdout 只能包含单个 JSON 文档，
/// 人类可读诊断一律写 stderr。
/// </summary>
public static class CliOutput
{
    private static readonly JsonSerializerOptions s_options = CreateOptions();

    /// <summary>序列化操作结果，保留实际泛型 Data 形状。</summary>
    public static string SerializeResult(OperationResult result)
    {
        return JsonSerializer.Serialize(result, result.GetType(), s_options);
    }

    /// <summary>序列化任意协议 DTO；活动查询结果沿用同一套 camelCase 与字符串枚举。</summary>
    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, s_options);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        // 与 Activity 文件保持一致：属性 camelCase，枚举值也使用 camelCase 字符串。
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
