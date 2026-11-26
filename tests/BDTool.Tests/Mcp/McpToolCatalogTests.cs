using System;
using System.IO;
using System.Linq;
using BDTool.Contracts.Operations;
using BDTool.Mcp;
using BDTool.Operations.Catalog;

namespace BDTool.Tests.Mcp;

/// <summary>验证 MCP 广告的工具集合完全来自 catalog，不包含 Provider 或桥接层私有工具。</summary>
public sealed class McpToolCatalogTests
{
    [Fact]
    public void CreateList_ExposesCatalogOperationsWithIdenticalSchemas()
    {
        var catalog = OperationCatalogLoader.Load(
            Path.Combine(AppContext.BaseDirectory, "catalog", "operations.json"));
        var expected = catalog.GetAll()
            .Where(item => item.Implemented
                && string.Equals(item.Availability, OperationAvailability.Available, StringComparison.Ordinal))
            .ToArray();

        var list = McpToolCatalog.CreateList(catalog);

        Assert.Equal(expected.Select(item => item.Id), list.Tools.Select(tool => tool.Name));
        Assert.Equal(expected.Length, list.Tools.Count);
        foreach (var descriptor in expected)
        {
            var tool = Assert.Single(list.Tools, item => item.Name == descriptor.Id);
            Assert.True(
                JsonElementDeepEquals(tool.InputSchema, descriptor.InputSchemaJson),
                "输入 Schema 不一致：" + descriptor.Id);
            Assert.True(
                JsonElementDeepEquals(tool.OutputSchema!.Value, descriptor.ResultSchemaJson),
                "输出 Schema 不一致：" + descriptor.Id);
            Assert.DoesNotContain("provider", tool.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unity_cli", tool.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool JsonElementDeepEquals(System.Text.Json.JsonElement element, string expectedJson)
    {
        using var expected = System.Text.Json.JsonDocument.Parse(expectedJson);
        return System.Text.Json.JsonElement.DeepEquals(element, expected.RootElement);
    }
}
