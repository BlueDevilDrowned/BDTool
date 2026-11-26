using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BDTool.Contracts.Operations;

namespace BDTool.Operations.Catalog;

/// <summary>
/// 从 JSON 文档加载操作目录。字段名与 catalog/operations.json 保持一致，
/// Schema 作为原文保存，不让 Contracts 依赖 JSON 库。
/// </summary>
public static class OperationCatalogLoader
{
    /// <summary>读取并解析目录文件；文件缺失属于环境未就绪，抛目录异常。</summary>
    public static OperationCatalog Load(string catalogPath)
    {
        if (string.IsNullOrWhiteSpace(catalogPath))
        {
            throw new OperationCatalogException("操作目录路径不能为空。");
        }

        if (!File.Exists(catalogPath))
        {
            throw new OperationCatalogException($"未找到操作目录文件：{catalogPath}");
        }

        string json;
        try
        {
            json = File.ReadAllText(catalogPath);
        }
        catch (IOException exception)
        {
            throw new OperationCatalogException($"读取操作目录失败：{exception.Message}", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new OperationCatalogException($"没有读取操作目录的权限：{exception.Message}", exception);
        }

        return Parse(json);
    }

    /// <summary>解析目录 JSON。所有结构错误合并为一条可读消息抛出。</summary>
    public static OperationCatalog Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new OperationCatalogException("操作目录内容为空。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new OperationCatalogException($"操作目录不是合法 JSON：{exception.Message}", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new OperationCatalogException("操作目录根节点必须是对象。");
            }

            if (!root.TryGetProperty("version", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var version))
            {
                throw new OperationCatalogException("操作目录缺少合法的整数 version。");
            }

            if (!root.TryGetProperty("operations", out var operationsElement)
                || operationsElement.ValueKind != JsonValueKind.Array)
            {
                throw new OperationCatalogException("操作目录缺少 operations 数组。");
            }

            var descriptors = new List<OperationDescriptor>();
            var index = 0;
            foreach (var item in operationsElement.EnumerateArray())
            {
                descriptors.Add(ReadDescriptor(item, index));
                index++;
            }

            return new OperationCatalog(version, descriptors);
        }
    }

    private static OperationDescriptor ReadDescriptor(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new OperationCatalogException($"operations[{index}] 必须是对象。");
        }

        var id = ReadRequiredString(element, "id", index);
        var descriptor = new OperationDescriptor
        {
            Id = id,
            Description = ReadRequiredString(element, "description", index),
            Effect = ReadRequiredString(element, "effect", index),
            Environment = ReadStringArray(element, "environment", index),
            RequiredCapabilities = ReadStringArray(element, "requiredCapabilities", index),
            InputSchemaJson = ReadRequiredObjectJson(element, "inputSchema", index),
            ResultSchemaJson = ReadRequiredObjectJson(element, "resultSchema", index),
            TimeoutSeconds = ReadRequiredInt32(element, "timeoutSeconds", index),
            Artifacts = ReadStringArray(element, "artifacts", index),
            Implemented = ReadRequiredBoolean(element, "implemented", index),
            Milestone = ReadRequiredString(element, "milestone", index),
            Availability = ReadRequiredString(element, "availability", index),
            Notes = ReadOptionalString(element, "notes")
        };

        var errors = OperationDescriptorValidator.Validate(descriptor);
        if (errors.Count > 0)
        {
            throw new OperationCatalogException(
                $"operations[{index}]（{descriptor.Id}）不合法：{string.Join("；", errors)}");
        }

        return descriptor;
    }

    private static string ReadRequiredString(JsonElement element, string name, int index)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new OperationCatalogException($"operations[{index}].{name} 必须是字符串。");
        }

        return value.GetString() ?? string.Empty;
    }

    private static string ReadOptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return value.GetString() ?? string.Empty;
    }

    private static bool ReadRequiredBoolean(JsonElement element, string name, int index)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new OperationCatalogException($"operations[{index}].{name} 必须是布尔值。");
        }

        return value.GetBoolean();
    }

    private static int ReadRequiredInt32(JsonElement element, string name, int index)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result))
        {
            throw new OperationCatalogException($"operations[{index}].{name} 必须是整数。");
        }

        return result;
    }

    private static List<string> ReadStringArray(JsonElement element, string name, int index)
    {
        var result = new List<string>();
        if (!element.TryGetProperty(name, out var value))
        {
            return result;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new OperationCatalogException($"operations[{index}].{name} 必须是字符串数组。");
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new OperationCatalogException($"operations[{index}].{name} 只能包含字符串。");
            }

            result.Add(item.GetString() ?? string.Empty);
        }

        return result;
    }

    private static string ReadRequiredObjectJson(JsonElement element, string name, int index)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new OperationCatalogException($"operations[{index}].{name} 必须是 JSON 对象。");
        }

        return value.GetRawText();
    }
}
