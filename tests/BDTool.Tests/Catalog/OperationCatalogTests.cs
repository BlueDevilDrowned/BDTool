using System;
using System.Linq;
using System.Text.Json;
using BDTool.Contracts.Operations;
using BDTool.Operations.Catalog;
using BDTool.Tests.Support;

namespace BDTool.Tests.Catalog;

/// <summary>验证真实操作目录，以及加载器的拒绝规则。</summary>
public sealed class OperationCatalogTests
{
    [Fact]
    public void RealCatalog_MarksOnlyRegisteredDoctorAsImplemented()
    {
        var catalog = OperationCatalogLoader.Load(RepoPaths.CatalogFile);
        var descriptors = catalog.GetAll();

        Assert.Equal(
            new[] { "operations.describe", "operations.list", "system.doctor" },
            descriptors.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal));

        var doctor = Assert.Single(descriptors, item => item.Id == "system.doctor");
        Assert.True(doctor.Implemented);
        Assert.Equal(OperationAvailability.Available, doctor.Availability);
        Assert.Equal(OperationEffects.Read, doctor.Effect);
        Assert.Equal("M0", doctor.Milestone);

        foreach (var plannedId in new[] { "operations.list", "operations.describe" })
        {
            var planned = Assert.Single(descriptors, item => item.Id == plannedId);
            Assert.False(planned.Implemented);
            Assert.NotEqual(OperationAvailability.Available, planned.Availability);
            Assert.Equal("M1", planned.Milestone);
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

    [Fact]
    public void RealCatalog_OnlyDoctorClaimsAvailability()
    {
        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(RepoPaths.CatalogFile));
        var operations = document.RootElement.GetProperty("operations").EnumerateArray().ToArray();

        var implemented = operations
            .Where(item => item.GetProperty("implemented").GetBoolean())
            .ToArray();
        var doctor = Assert.Single(implemented);
        Assert.Equal("system.doctor", doctor.GetProperty("id").GetString());
        Assert.Equal(OperationAvailability.Available, doctor.GetProperty("availability").GetString());

        foreach (var planned in operations.Where(item => !item.GetProperty("implemented").GetBoolean()))
        {
            Assert.NotEqual(OperationAvailability.Available, planned.GetProperty("availability").GetString());
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
