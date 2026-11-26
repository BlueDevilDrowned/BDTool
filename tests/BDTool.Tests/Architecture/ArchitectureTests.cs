using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BDTool.Tests.Support;

namespace BDTool.Tests.Architecture;

/// <summary>
/// 依赖方向验收：直接解析真实 .csproj 的 ProjectReference 与 PackageReference，
/// 不维护硬编码依赖表，避免工程文件漂移后测试仍然通过。
/// </summary>
public sealed class ArchitectureTests
{
    [Fact]
    public void Contracts_HasNoProjectOrPackageReferences()
    {
        var document = XDocument.Load(RepoPaths.ProjectFile("BDTool.Contracts"));

        Assert.Empty(GetProjectReferences(document));
        Assert.Empty(GetPackageReferences(document));
        Assert.Equal("9.0", GetProperty(document, "LangVersion"));
    }

    [Fact]
    public void Core_DependsOnlyOnContracts()
    {
        var references = GetReferences("BDTool.Core");

        Assert.Equal(new[] { "BDTool.Contracts" }, references.OrderBy(x => x));
    }

    [Fact]
    public void Operations_DoesNotDependOnAnyProvider()
    {
        var references = GetReferences("BDTool.Operations");

        Assert.DoesNotContain(references, name => name.Contains("Provider", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            new[] { "BDTool.Contracts", "BDTool.Core" },
            references.OrderBy(x => x));
    }

    [Fact]
    public void Application_And_Cli_DoNotDependOnProvider()
    {
        foreach (var projectName in new[] { "BDTool.Application", "BDTool.Cli", "BDTool.Composition" })
        {
            var references = GetReferences(projectName);
            Assert.DoesNotContain(
                references,
                name => name.Contains("Provider", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Application_DependsOnContractsCoreOperationsAndActivity()
    {
        var references = GetReferences("BDTool.Application");

        Assert.Equal(
            new[] { "BDTool.Activity", "BDTool.Contracts", "BDTool.Core", "BDTool.Operations" },
            references.OrderBy(x => x));
    }

    [Fact]
    public void Cli_IsNamedBdtool_AndComposesThroughComposition()
    {
        var document = XDocument.Load(RepoPaths.ProjectFile("BDTool.Cli"));

        Assert.Equal("bdtool", GetProperty(document, "AssemblyName"));
        var references = GetProjectReferences(document).OrderBy(x => x).ToArray();
        Assert.Equal(
            new[] { "BDTool.Application", "BDTool.Composition", "BDTool.Contracts" },
            references);
    }

    [Fact]
    public void Contracts_SourcesDoNotReferenceJsonHttpMcpOrUnity()
    {
        var directory = Path.Combine(RepoPaths.Root, "src", "BDTool.Contracts");
        var forbidden = new[]
        {
            "System.Text.Json",
            "System.Net.Http",
            "System.Net.WebSockets",
            "System.Net.Sockets",
            "ModelContextProtocol",
            "UnityEditor",
            "UnityEngine"
        };

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("using ", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var prefix in forbidden)
                {
                    if (trimmed.IndexOf(prefix, StringComparison.Ordinal) >= 0)
                    {
                        violations.Add($"{Path.GetFileName(file)}: {trimmed}");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Contracts_DoNotExposeUniversalObjectBusinessResults()
    {
        var directory = Path.Combine(RepoPaths.Root, "src", "BDTool.Contracts");
        var sources = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();

        Assert.DoesNotContain(sources, text => text.Contains("OperationResult<object>", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, text => text.Contains("public object Data", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, text => text.Contains("Dictionary<string, object", StringComparison.Ordinal));
    }

    [Fact]
    public void SolutionContainsExactlyTheEightM0Projects()
    {
        var solution = File.ReadAllText(Path.Combine(RepoPaths.Root, "BDTool.sln"));
        var expected = new[]
        {
            "BDTool.Contracts",
            "BDTool.Core",
            "BDTool.Operations",
            "BDTool.Activity",
            "BDTool.Application",
            "BDTool.Composition",
            "BDTool.Cli",
            "BDTool.Tests"
        };

        foreach (var project in expected)
        {
            Assert.Contains(project + ".csproj", solution, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(Path.Combine(RepoPaths.Root, "BDTool.slnx")));
    }

    private static string[] GetReferences(string projectName)
    {
        return GetProjectReferences(XDocument.Load(RepoPaths.ProjectFile(projectName)));
    }

    private static string[] GetProjectReferences(XDocument document)
    {
        return document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFileNameWithoutExtension(value!.Replace('\\', '/')))
            .ToArray();
    }

    private static string[] GetPackageReferences(XDocument document)
    {
        return document.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static string GetProperty(XDocument document, string name)
    {
        return document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == name)?.Value
            ?? string.Empty;
    }
}
