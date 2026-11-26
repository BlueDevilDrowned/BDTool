using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BDTool.Contracts.Operations;

namespace BDTool.Application.Serialization;

/// <summary>
/// CLI 与 MCP 共用的操作结果序列化器。两者只能改变展示方式的包装，
/// 不能让同一操作的字段名、枚举值或空 data 形状发生漂移。
/// </summary>
public static class OperationResultJsonSerializer
{
    private static readonly JsonSerializerOptions s_options = CreateOptions();

    /// <summary>
    /// 序列化为缩进的统一信封。非泛型失败没有 data 属性时补空对象，
    /// 避免调用方必须区分 data 缺失与 data=null。
    /// </summary>
    public static string Serialize(OperationResult result)
    {
        return CreateEnvelope(result).ToJsonString(s_options);
    }

    /// <summary>序列化为 MCP structuredContent 使用的 JSON 元素。</summary>
    public static JsonElement SerializeToElement(OperationResult result)
    {
        return JsonSerializer.SerializeToElement(CreateEnvelope(result), s_options);
    }

    private static JsonObject CreateEnvelope(OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var document = JsonSerializer.SerializeToNode(result, result.GetType(), s_options) as JsonObject
            ?? new JsonObject();
        if (!document.TryGetPropertyValue("data", out var data) || data is null)
        {
            document["data"] = new JsonObject();
        }

        return document;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
