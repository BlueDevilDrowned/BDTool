using System.Text.Json;
using BDTool.Contracts.Operations;
using ModelContextProtocol.Protocol;

namespace BDTool.Mcp;

/// <summary>
/// 把可用 catalog 操作投影为 MCP Tool。工具名保持 operation ID，
/// schema 直接复用目录原文，绝不从 Provider 或编辑器工具名生成 MCP 工具。
/// </summary>
internal static class McpToolCatalog
{
    public static ListToolsResult CreateList(IOperationCatalog catalog)
    {
        var tools = catalog.GetAll()
            .Where(IsExposed)
            .Select(CreateTool)
            .ToList();
        return new ListToolsResult { Tools = tools };
    }

    private static bool IsExposed(OperationDescriptor descriptor)
    {
        return descriptor.Implemented
            && string.Equals(descriptor.Availability, OperationAvailability.Available, StringComparison.Ordinal);
    }

    private static Tool CreateTool(OperationDescriptor descriptor)
    {
        return new Tool
        {
            Name = descriptor.Id,
            Title = descriptor.Id,
            Description = descriptor.Description,
            InputSchema = ParseSchema(descriptor.InputSchemaJson),
            OutputSchema = ParseSchema(descriptor.ResultSchemaJson),
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = string.Equals(descriptor.Effect, OperationEffects.Read, StringComparison.Ordinal),
                DestructiveHint = string.Equals(descriptor.Effect, OperationEffects.Destructive, StringComparison.Ordinal),
                IdempotentHint = string.Equals(descriptor.Effect, OperationEffects.Read, StringComparison.Ordinal),
                OpenWorldHint = false
            }
        };
    }

    private static JsonElement ParseSchema(string schema)
    {
        using var document = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(schema) ? "{}" : schema);
        return document.RootElement.Clone();
    }
}
