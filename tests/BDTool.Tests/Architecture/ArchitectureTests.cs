using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Projects;
using BDTool.Contracts.Providers;
using BDTool.Contracts.Workspace;
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
        foreach (var projectName in new[] { "BDTool.Application", "BDTool.Cli" })
        {
            AssertNoProviderReference(projectName);
        }
    }

    [Fact]
    public void Composition_IsTheOnlyApplicationSideProviderAssembly()
    {
        var compositionReferences = GetReferences("BDTool.Composition");
        Assert.Contains("BDTool.Provider.Local", compositionReferences);
        Assert.Contains("BDTool.Provider.Akio", compositionReferences);

        var sourceRoot = Path.Combine(RepoPaths.Root, "src");
        foreach (var projectFile in Directory.EnumerateFiles(sourceRoot, "BDTool.*.csproj", SearchOption.AllDirectories))
        {
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            if (projectName == "BDTool.Composition" || projectName.StartsWith("BDTool.Provider.", StringComparison.Ordinal))
            {
                continue;
            }

            AssertNoProviderReference(projectName);
        }
    }

    [Fact]
    public void LocalProvider_IsRestrictedToContractsAndCore()
    {
        var projectFile = Path.Combine(
            RepoPaths.Root,
            "src",
            "BDTool.Provider.Local",
            "BDTool.Provider.Local.csproj");
        Assert.True(File.Exists(projectFile), "M1 必须包含 BDTool.Provider.Local 项目。");

        var references = GetProjectReferences(XDocument.Load(projectFile));
        Assert.Equal(
            new[] { "BDTool.Contracts", "BDTool.Core" },
            references.OrderBy(x => x));

        var solution = File.ReadAllText(Path.Combine(RepoPaths.Root, "BDTool.sln"));
        Assert.Contains("BDTool.Provider.Local.csproj", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void AkioProvider_DependsOnlyOnContracts()
    {
        var projectFile = Path.Combine(
            RepoPaths.Root,
            "src",
            "BDTool.Provider.Akio",
            "BDTool.Provider.Akio.csproj");
        Assert.True(File.Exists(projectFile), "M2 必须包含 BDTool.Provider.Akio 项目。");

        var references = GetProjectReferences(XDocument.Load(projectFile));
        Assert.Equal(new[] { "BDTool.Contracts" }, references.OrderBy(x => x));

        var solution = File.ReadAllText(Path.Combine(RepoPaths.Root, "BDTool.sln"));
        Assert.Contains("BDTool.Provider.Akio.csproj", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderProjects_DoNotReferenceApplicationOrOtherProviders()
    {
        var sourceRoot = Path.Combine(RepoPaths.Root, "src");
        foreach (var projectFile in Directory.EnumerateFiles(sourceRoot, "BDTool.Provider.*.csproj", SearchOption.AllDirectories))
        {
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            var references = GetProjectReferences(XDocument.Load(projectFile));

            Assert.DoesNotContain("BDTool.Application", references);
            Assert.DoesNotContain(
                references,
                name => name.StartsWith("BDTool.Provider.", StringComparison.Ordinal) && name != projectName);
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
    public void Cli_SkillsInstall_StaysProviderFreeAndUsesComposition()
    {
        AssertNoProviderReference("BDTool.Cli");

        // CLI 只能经组合根安装入口调用 Skill；直接构造 Core 安装器会把路径解析、
        // 冲突预检和错误映射散落到表现层。
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Root,
            "src",
            "BDTool.Cli",
            "CliEntrypoint.cs"));
        Assert.Contains("BDToolComposition.InstallProjectSkills", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new SkillInstaller", source, StringComparison.Ordinal);
        Assert.DoesNotContain("using BDTool.Core.Skills", source, StringComparison.Ordinal);

        // 发布 Skill 必须作为 CLI 输出内容复制，并通过 Link 保留 references 层级。
        var document = XDocument.Load(RepoPaths.ProjectFile("BDTool.Cli"));
        var contentIncludes = document.Descendants()
            .Where(element => element.Name.LocalName == "Content")
            .Select(element => ((string?)element.Attribute("Include"))?.Replace('\\', '/'))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        Assert.Contains(contentIncludes, value => value!.EndsWith("skills/**/*", StringComparison.Ordinal));
    }

    [Fact]
    public void Contracts_SourcesDoNotReferenceJsonHttpMcpOrUnity()
    {
        var directory = Path.Combine(RepoPaths.Root, "src", "BDTool.Contracts");
        var forbidden = new[]
        {
            "System.Text.Json",
            "System.IO",
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
    public void Operations_SourcesDoNotReferenceProcessNetworkUnityOrProviders()
    {
        // Operations 只能通过 Contracts 端口访问外部世界。直接用进程、网络或 Unity 类型
        // 会绕过 Editor/Workspace Provider 的能力与审计边界，必须在架构层拦住。
        var directory = Path.Combine(RepoPaths.Root, "src", "BDTool.Operations");
        var forbidden = new[]
        {
            "System.Diagnostics",
            "System.Net",
            "UnityEditor",
            "UnityEngine",
            "BDTool.Provider"
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
    public void Contracts_M1WorkspaceAndOperationExtensionsAreFrozen()
    {
        Assert.Equal("workspace.read", CapabilityIds.WorkspaceRead);
        Assert.Equal("workspace.write", CapabilityIds.WorkspaceWrite);
        Assert.Equal("workspace.search", CapabilityIds.WorkspaceSearch);
        Assert.Equal("workspace.fingerprint", CapabilityIds.WorkspaceFingerprint);
        Assert.Equal("editor.status", CapabilityIds.EditorStatus);
        Assert.Equal("PRECONDITION_FAILED", ErrorCodes.PreconditionFailed);

        var read = new WorkspaceReadResult();
        var search = new WorkspaceSearchResult();
        var fingerprint = new WorkspaceFileFingerprint();
        var change = new WorkspaceChangeResult();

        Assert.Equal(0, read.Length);
        Assert.Equal(0, search.SkippedBinary);
        Assert.Equal(0, search.SkippedTooLarge);
        Assert.Equal(0, search.SkippedSymlink);
        Assert.False(fingerprint.Exists);
        Assert.False(change.Created);
        Assert.False(change.DryRun);
    }

    [Fact]
    public void Contracts_M1OperationViewsAndOfflineContextRemainTyped()
    {
        var list = new OperationsListData();
        list.Operations.Add(new OperationSummary());
        var describe = new OperationsDescribeData();
        var detail = new OperationDetail
        {
            Id = "workspace.read",
            InputSchemaJson = "{}",
            ResultSchemaJson = "{}"
        };
        describe.Operation = detail;

        Assert.Single(list.Operations);
        Assert.Same(detail, describe.Operation);
        Assert.Equal("{}", describe.Operation.InputSchemaJson);

        var context = new ProjectContextData();
        Assert.False(context.Editor.Connected);
        Assert.Equal("not-connected", context.Editor.Status);
    }

    [Fact]
    public void SolutionContainsM1BaselineProjects()
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
            "BDTool.Provider.Local",
            "BDTool.Provider.Akio",
            "BDTool.Tests"
        };

        foreach (var project in expected)
        {
            Assert.Contains(project + ".csproj", solution, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(Path.Combine(RepoPaths.Root, "BDTool.slnx")));
    }

    private static void AssertNoProviderReference(string projectName)
    {
        var references = GetReferences(projectName);
        Assert.DoesNotContain(
            references,
            name => name.Contains("Provider", StringComparison.OrdinalIgnoreCase));
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
