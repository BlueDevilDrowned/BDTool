using System;
using System.Collections.Generic;
using System.Text.Json;

namespace BDTool.Operations.Input;

/// <summary>
/// 操作输入的严格 JSON 解析器。所有输入必须是对象，注释、尾逗号、重复字段和
/// 未声明字段一律拒绝，避免拼写错误被静默忽略后产生误操作。
/// </summary>
public static class OperationInputParser
{
    private static readonly JsonDocumentOptions s_documentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    /// <summary>解析对象并校验字段集合；调用方负责释放返回的文档。</summary>
    public static JsonDocument ParseObject(string? inputJson, params string[] allowedProperties)
    {
        var text = string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson!;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, s_documentOptions);
        }
        catch (JsonException exception)
        {
            throw new OperationInputException("输入不是合法 JSON：" + exception.Message, exception);
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new OperationInputException("输入必须是 JSON 对象。");
        }

        var allowed = new HashSet<string>(allowedProperties, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            // JsonElement 的名称在文档释放后会失效；先复制名称再做释放与诊断。
            var propertyName = property.Name;
            if (!allowed.Contains(propertyName))
            {
                document.Dispose();
                throw new OperationInputException("输入包含未知字段：" + propertyName);
            }

            if (!seen.Add(propertyName))
            {
                document.Dispose();
                throw new OperationInputException("输入包含重复字段：" + propertyName);
            }
        }

        return document;
    }

    /// <summary>读取必填非空字符串。</summary>
    public static string RequireString(JsonElement root, string propertyName)
    {
        var value = GetString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new OperationInputException($"字段 {propertyName} 不能为空。");
        }

        return value;
    }

    /// <summary>
    /// 读取必填字符串，但允许空字符串。用于 content 这类“空值本身有语义”的字段：
    /// 字段缺失说明调用方漏传参数，必须拒绝；字段显式为 "" 才代表调用方要求清空文件。
    /// 两者一旦合并成同一个空字符串，漏字段的请求就会静默清空目标文件。
    /// </summary>
    public static string RequireStringAllowEmpty(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            throw new OperationInputException($"字段 {propertyName} 不能缺失。");
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new OperationInputException($"字段 {propertyName} 必须是字符串。");
        }

        return value.GetString() ?? string.Empty;
    }

    /// <summary>读取可选字符串；字段缺失时返回 null。</summary>
    public static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new OperationInputException($"字段 {propertyName} 必须是字符串。");
        }

        return value.GetString();
    }

    /// <summary>读取可空字符串，并把显式 null 视为缺失。</summary>
    public static string? GetNullableString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new OperationInputException($"字段 {propertyName} 必须是字符串或 null。");
        }

        return value.GetString();
    }

    /// <summary>读取可选布尔值。</summary>
    public static bool GetBoolean(JsonElement root, string propertyName, bool defaultValue = false)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new OperationInputException($"字段 {propertyName} 必须是布尔值。")
        };
    }

    /// <summary>读取可选整数。</summary>
    public static int GetInt32(JsonElement root, string propertyName, int defaultValue)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw new OperationInputException($"字段 {propertyName} 必须是 32 位整数。");
        }

        return number;
    }

    /// <summary>
    /// 读取必填字符串数组：字段必须存在且是数组，元素必须全部是字符串。
    /// 空数组合法（调用方明确给出零个元素）；缺失或显式 null 都视为输入错误，
    /// 避免把丢失字段静默折叠成空集合后让 Provider 返回“全部成功”的假象。
    /// </summary>
    public static List<string> RequireStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            throw new OperationInputException($"字段 {propertyName} 不能缺失。");
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new OperationInputException($"字段 {propertyName} 必须是字符串数组。");
        }

        return ReadStringArray(value, propertyName);
    }

    private static List<string> ReadStringArray(JsonElement value, string propertyName)
    {
        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new OperationInputException($"字段 {propertyName} 必须是字符串数组。");
            }

            result.Add(item.GetString() ?? string.Empty);
        }

        return result;
    }
}
