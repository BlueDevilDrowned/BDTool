using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;
using BDTool.Operations.Catalog;
using BDTool.Tests.Support;

namespace BDTool.Tests.Catalog;

/// <summary>验证真实操作目录，以及加载器的拒绝规则。</summary>
public sealed class OperationCatalogTests
{
    [Fact]
    public void RealCatalog_ContainsEightAvailableM1Operations()
    {
        var catalog = OperationCatalogLoader.Load(RepoPaths.CatalogFile);
        var descriptors = catalog.GetAll();

        Assert.Equal(
            new[]
            {
                "operations.describe",
                "operations.list",
                "project.context",
                "system.doctor",
                "workspace.apply",
                "workspace.fingerprint",
                "workspace.read",
                "workspace.search"
            },
            descriptors.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal));

        // M1 的八个操作全部有真实 Handler；目录不得保留“已实现但 planned”或反向漂移。
        foreach (var descriptor in descriptors)
        {
            Assert.True(descriptor.Implemented, descriptor.Id + " 必须标记 implemented。");
            Assert.Equal(OperationAvailability.Available, descriptor.Availability);
            Assert.Equal("M1", descriptor.Milestone);
        }

        var doctor = Assert.Single(descriptors, item => item.Id == "system.doctor");
        Assert.Equal(OperationEffects.Read, doctor.Effect);
    }

    [Fact]
    public void RealCatalog_AllInputSchemasAreClosedAndResultSchemasUseEnvelope()
    {
        var catalog = OperationCatalogLoader.Load(RepoPaths.CatalogFile);

        foreach (var descriptor in catalog.GetAll())
        {
            using var input = JsonDocument.Parse(descriptor.InputSchemaJson);
            Assert.Equal(JsonValueKind.Object, input.RootElement.ValueKind);
            Assert.True(
                input.RootElement.TryGetProperty("additionalProperties", out var additional),
                descriptor.Id + " 的 inputSchema 必须声明 additionalProperties。");
            Assert.False(additional.GetBoolean(), descriptor.Id + " 必须关闭未知输入字段。");

            using var result = JsonDocument.Parse(descriptor.ResultSchemaJson);
            Assert.Equal(JsonValueKind.Object, result.RootElement.ValueKind);
            var required = result.RootElement.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray();
            Assert.Contains("status", required);
            Assert.Contains("data", required);

            // 完整信封的 data 属性位于 properties 下；required 只列出属性名。
            var data = result.RootElement.GetProperty("properties").GetProperty("data");
            Assert.Equal(JsonValueKind.Object, data.ValueKind);
            Assert.NotEmpty(data.GetProperty("required").EnumerateArray());
        }
    }

    [Fact]
    public void RealCatalog_WorkspaceOperationsDeclareMatchingCapabilities()
    {
        var catalog = OperationCatalogLoader.Load(RepoPaths.CatalogFile);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspace.read"] = CapabilityIds.WorkspaceRead,
            ["workspace.search"] = CapabilityIds.WorkspaceSearch,
            ["workspace.fingerprint"] = CapabilityIds.WorkspaceFingerprint,
            ["workspace.apply"] = CapabilityIds.WorkspaceWrite
        };

        foreach (var pair in expected)
        {
            var descriptor = Assert.Single(catalog.GetAll(), item => item.Id == pair.Key);
            Assert.Equal(new[] { pair.Value }, descriptor.RequiredCapabilities);
        }

        var apply = Assert.Single(catalog.GetAll(), item => item.Id == "workspace.apply");
        Assert.Equal(OperationEffects.WorkspaceWrite, apply.Effect);

        // operations/project.context 是纯应用侧读取，不应把本地 Provider 能力当作前置条件。
        foreach (var id in new[] { "operations.list", "operations.describe", "project.context", "system.doctor" })
        {
            var descriptor = Assert.Single(catalog.GetAll(), item => item.Id == id);
            Assert.Empty(descriptor.RequiredCapabilities);
            Assert.Equal(OperationEffects.Read, descriptor.Effect);
        }
    }

    [Fact]
    public void RealCatalog_SchemasAreJsonObjects()
    {
        var catalog = OperationCatalogLoader.Load(RepoPaths.CatalogFile);

        foreach (var descriptor in catalog.GetAll())
        {
            using var input = JsonDocument.Parse(descriptor.InputSchemaJson);
            using var result = JsonDocument.Parse(descriptor.ResultSchemaJson);
            Assert.Equal(JsonValueKind.Object, input.RootElement.ValueKind);
            Assert.Equal(JsonValueKind.Object, result.RootElement.ValueKind);
        }
    }

    /// <summary>
    /// inputSchema 必须与 Handler 的真实输入契约一致：required 只表达 Handler 必填字段，
    /// properties 只表达 Handler 接受的字段。projectPath 由组合根绑定，不属于操作输入，
    /// workspace schema 不得再声明它，否则会重新出现“声明了却被忽略”的漂移。
    /// </summary>
    [Fact]
    public void RealCatalog_InputSchemasMatchHandlerContract()
    {
        var catalog = OperationCatalogLoader.Load(RepoPaths.CatalogFile);
        var expected = new Dictionary<string, (string[] Required, string[] Properties)>(StringComparer.Ordinal)
        {
            ["system.doctor"] = (Array.Empty<string>(), Array.Empty<string>()),
            ["operations.list"] = (Array.Empty<string>(), Array.Empty<string>()),
            ["operations.describe"] = (new[] { "operationId" }, new[] { "operationId" }),
            ["project.context"] = (Array.Empty<string>(), Array.Empty<string>()),
            ["workspace.read"] = (new[] { "relativePath" }, new[] { "relativePath" }),
            ["workspace.search"] = (
                new[] { "query" },
                new[] { "query", "glob", "maxResults" }),
            ["workspace.fingerprint"] = (new[] { "relativePaths" }, new[] { "relativePaths" }),
            ["workspace.apply"] = (
                new[] { "relativePath", "content" },
                new[] { "relativePath", "content", "expectedSha256", "createIfMissing", "dryRun" })
        };

        Assert.Equal(
            expected.Keys.OrderBy(id => id, StringComparer.Ordinal),
            catalog.GetAll().Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal));

        foreach (var descriptor in catalog.GetAll())
        {
            using var schema = JsonDocument.Parse(descriptor.InputSchemaJson);
            var root = schema.RootElement;

            var required = root.TryGetProperty("required", out var requiredElement)
                ? requiredElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty)
                : Enumerable.Empty<string>();
            var properties = root.GetProperty("properties")
                .EnumerateObject()
                .Select(item => item.Name)
                .ToArray();

            var expectation = expected[descriptor.Id];
            Assert.Equal(
                expectation.Required.OrderBy(name => name, StringComparer.Ordinal),
                required.OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal(
                expectation.Properties.OrderBy(name => name, StringComparer.Ordinal),
                properties.OrderBy(name => name, StringComparer.Ordinal));
            Assert.DoesNotContain("projectPath", properties);
        }
    }

    [Fact]
    public void RealCatalog_AvailabilityMatchesImplementedFlags()
    {
        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(RepoPaths.CatalogFile));
        var operations = document.RootElement.GetProperty("operations").EnumerateArray().ToArray();

        foreach (var operation in operations)
        {
            var implemented = operation.GetProperty("implemented").GetBoolean();
            var availability = operation.GetProperty("availability").GetString();
            Assert.Equal(implemented, string.Equals(availability, OperationAvailability.Available, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DuplicateOperationIds_AreRejected()
    {
        var json = BuildCatalog(Operation("system.doctor"), Operation("system.doctor"));

        var exception = Assert.Throws<OperationCatalogException>(() => OperationCatalogLoader.Parse(json));
        Assert.Contains("重复", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImplementedMustMatchAvailability()
    {
        var implementedButPlanned = BuildCatalog(Operation("system.doctor", implemented: true));
        var availableButNotImplemented = BuildCatalog(
            Operation("system.doctor", implemented: false, availability: OperationAvailability.Available));

        Assert.Throws<OperationCatalogException>(() => OperationCatalogLoader.Parse(implementedButPlanned));
        Assert.Throws<OperationCatalogException>(() => OperationCatalogLoader.Parse(availableButNotImplemented));
    }

    [Fact]
    public void InvalidIdAndUnknownEffect_AreRejected()
    {
        var badId = BuildCatalog(Operation("System.Doctor"));
        var badEffect = BuildCatalog(Operation("system.doctor", effect: "network"));

        Assert.Throws<OperationCatalogException>(() => OperationCatalogLoader.Parse(badId));
        Assert.Throws<OperationCatalogException>(() => OperationCatalogLoader.Parse(badEffect));
    }

    [Fact]
    public void NonObjectSchema_IsRejected()
    {
        var json = BuildCatalog(Operation("system.doctor").Replace(
            "\"inputSchema\": { \"type\": \"object\" }",
            "\"inputSchema\": []",
            StringComparison.Ordinal));

        Assert.Throws<OperationCatalogException>(() => OperationCatalogLoader.Parse(json));
    }

    private static string Operation(
        string id,
        bool implemented = false,
        string availability = OperationAvailability.Planned,
        string effect = "read") =>
        $@"{{
            ""id"": ""{id}"",
            ""description"": ""测试操作"",
            ""effect"": ""{effect}"",
            ""environment"": [""workspace""],
            ""requiredCapabilities"": [],
            ""inputSchema"": {{ ""type"": ""object"" }},
            ""resultSchema"": {{ ""type"": ""object"" }},
            ""timeoutSeconds"": 30,
            ""artifacts"": [],
            ""implemented"": {(implemented ? "true" : "false")},
            ""milestone"": ""M0"",
            ""availability"": ""{availability}""
        }}";

    private static string BuildCatalog(params string[] operations) =>
        $@"{{ ""version"": 1, ""operations"": [{string.Join(",", operations)}] }}";
}
