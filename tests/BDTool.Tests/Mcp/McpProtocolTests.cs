using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BDTool.Cli;
using BDTool.Mcp;
using BDTool.Tests.Support;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace BDTool.Tests.Mcp;

/// <summary>
/// 使用官方 MCP SDK 启动真实 stdio 子进程，验证发现、调用、Resource、
/// Activity 来源以及与 CLI 的 Schema/结果一致性。
/// </summary>
public sealed class McpProtocolTests
{
    [Fact]
    public async Task StdioServer_DiscoversToolsAndResourcesAndExecutesProjectContext()
    {
        using var project = new TemporaryProjectDirectory();
        using var stderr = new StringWriter();
        await using var client = await CreateClientAsync(project.ProjectPath, stderr);

        var tools = await client.ListToolsAsync();
        Assert.Equal(12, tools.Count);
        Assert.Contains(tools, tool => tool.Name == "project.context");
        Assert.DoesNotContain(
            tools,
            tool => tool.Name.Contains("provider", StringComparison.OrdinalIgnoreCase)
                || tool.Name.Contains("unity_cli", StringComparison.OrdinalIgnoreCase));

        var resources = await client.ListResourcesAsync();
        Assert.Equal(new[] { McpResources.CatalogUri, McpResources.ProjectContextUri }, resources.Select(item => item.Uri));

        var catalogResult = await client.ReadResourceAsync(McpResources.CatalogUri);
        var catalogText = Assert.IsType<TextResourceContents>(Assert.Single(catalogResult.Contents)).Text;
        using (var catalog = JsonDocument.Parse(catalogText))
        {
            Assert.Equal(12, catalog.RootElement.GetProperty("operations").GetArrayLength());
        }

        var contextResult = await client.CallToolAsync(
            "project.context",
            new Dictionary<string, object?>());
        var contextText = Assert.IsType<TextContentBlock>(Assert.Single(contextResult.Content)).Text;
        using (var context = JsonDocument.Parse(contextText))
        {
            Assert.False(contextResult.IsError);
            Assert.Equal("completed", context.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                Path.GetFullPath(project.ProjectPath),
                context.RootElement.GetProperty("data").GetProperty("projectPath").GetString());
        }

        var contextResource = await client.ReadResourceAsync(McpResources.ProjectContextUri);
        var contextResourceText = Assert.IsType<TextResourceContents>(Assert.Single(contextResource.Contents)).Text;
        using (var context = JsonDocument.Parse(contextResourceText))
        {
            Assert.Equal("completed", context.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                Path.GetFullPath(project.ProjectPath),
                context.RootElement.GetProperty("data").GetProperty("projectPath").GetString());
        }

        Assert.True(
            contextResult.StructuredContent is not null,
            "MCP 工具必须同时返回 structuredContent。");
        Assert.True(Directory.Exists(project.ActivityRoot), stderr.ToString());
        var activities = Directory.EnumerateFiles(project.ActivityRoot, "*.json", SearchOption.AllDirectories).ToArray();
        Assert.Equal(2, activities.Length);
        foreach (var activity in activities)
        {
            using var activityDocument = JsonDocument.Parse(File.ReadAllText(activity));
            Assert.Equal("mcp", activityDocument.RootElement.GetProperty("source").GetString());
            Assert.Equal("project.context", activityDocument.RootElement.GetProperty("operationId").GetString());
        }
    }

    [Fact]
    public async Task StdioServer_ForwardsToolArgumentsAndPreservesValidationErrors()
    {
        using var project = new TemporaryProjectDirectory();
        const string relativePath = "Assets/read-me.txt";
        const string content = "mcp argument forwarding";
        var absolutePath = Path.Combine(project.ProjectPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, content);

        using var stderr = new StringWriter();
        await using var client = await CreateClientAsync(project.ProjectPath, stderr);

        var read = await client.CallToolAsync(
            "workspace.read",
            new Dictionary<string, object?> { ["relativePath"] = relativePath });
        var readText = Assert.IsType<TextContentBlock>(Assert.Single(read.Content)).Text;
        using (var document = JsonDocument.Parse(readText))
        {
            Assert.False(read.IsError);
            Assert.Equal(content, document.RootElement.GetProperty("data").GetProperty("content").GetString());
        }

        var invalid = await client.CallToolAsync(
            "workspace.read",
            new Dictionary<string, object?>
            {
                ["relativePath"] = relativePath,
                ["unexpected"] = true
            });
        var invalidText = Assert.IsType<TextContentBlock>(Assert.Single(invalid.Content)).Text;
        using (var document = JsonDocument.Parse(invalidText))
        {
            Assert.True(invalid.IsError);
            Assert.Equal(
                "INVALID_ARGUMENT",
                document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task CliAndMcp_ReturnSameWorkspaceResultEnvelope()
    {
        using var project = new TemporaryProjectDirectory();
        const string relativePath = "Assets/same-result.txt";
        var absolutePath = Path.Combine(project.ProjectPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, "same result");
        var inputJson = "{\"relativePaths\":[\"" + relativePath + "\"]}";

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var cliExit = await CliEntrypoint.RunAsync(
            new[] { "workspace.fingerprint", "--project", project.ProjectPath, "--input-json", inputJson },
            stdout,
            stderr);
        Assert.Equal(0, cliExit);

        using var serverStderr = new StringWriter();
        await using var client = await CreateClientAsync(project.ProjectPath, serverStderr);
        var mcpResult = await client.CallToolAsync(
            "workspace.fingerprint",
            new Dictionary<string, object?>
            {
                ["relativePaths"] = new[] { relativePath }
            });
        var mcpText = Assert.IsType<TextContentBlock>(Assert.Single(mcpResult.Content)).Text;

        using var cliJson = JsonDocument.Parse(stdout.ToString());
        using var mcpJson = JsonDocument.Parse(mcpText);
        Assert.True(
            JsonElement.DeepEquals(cliJson.RootElement, mcpJson.RootElement),
            "CLI 与 MCP 结果不一致。\nCLI=" + stdout + "\nMCP=" + mcpText);
    }

    [Fact]
    public async Task ConfigCommand_GeneratesJsonAndCodexToml()
    {
        using var project = new TemporaryProjectDirectory();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var jsonExit = await McpEntrypoint.RunAsync(
            new[] { "config", "--project", project.ProjectPath, "--format", "json" },
            stdout,
            stderr);

        Assert.Equal(0, jsonExit);
        Assert.Equal(string.Empty, stderr.ToString());
        using (var document = JsonDocument.Parse(stdout.ToString()))
        {
            var server = document.RootElement.GetProperty("mcpServers").GetProperty("bdtool");
            Assert.Equal("dotnet", server.GetProperty("command").GetString());
            Assert.Equal(Path.GetFullPath(project.ProjectPath), server.GetProperty("cwd").GetString());
            Assert.Contains(
                server.GetProperty("args").EnumerateArray(),
                value => value.GetString() == Path.GetFullPath(project.ProjectPath));
        }

        stdout.GetStringBuilder().Clear();
        var tomlExit = await McpEntrypoint.RunAsync(
            new[] { "config", "--project", project.ProjectPath, "--format", "codex" },
            stdout,
            stderr);
        Assert.Equal(0, tomlExit);
        Assert.Contains("[mcp_servers.bdtool]", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "args = [",
            stdout.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "cwd = ",
            stdout.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidArguments_ReturnExitTwo()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await McpEntrypoint.RunAsync(
            new[] { "config", "--format", "yaml" },
            stdout,
            stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("--format", stderr.ToString(), StringComparison.Ordinal);

        stdout.GetStringBuilder().Clear();
        stderr.GetStringBuilder().Clear();
        var providerFlagExit = await McpEntrypoint.RunAsync(
            new[] { "serve", "--unity-cli", "hidden.exe" },
            stdout,
            stderr);
        Assert.Equal(2, providerFlagExit);
        Assert.Contains("--unity-cli", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_ListsServeAndConfigCommands()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await McpEntrypoint.RunAsync(new[] { "--help" }, stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains("bdtool-mcp", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("config", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("--catalog", stdout.ToString(), StringComparison.Ordinal);

        stdout.GetStringBuilder().Clear();
        var versionExit = await McpEntrypoint.RunAsync(new[] { "--version" }, stdout, stderr);
        Assert.Equal(0, versionExit);
        Assert.Contains("bdtool-mcp 1.0.0", stdout.ToString(), StringComparison.Ordinal);
    }

    private static async Task<McpClient> CreateClientAsync(
        string projectPath,
        TextWriter stderr)
    {
        var serverPath = Path.Combine(AppContext.BaseDirectory, "bdtool-mcp.dll");
        Assert.True(File.Exists(serverPath), "测试输出中缺少 bdtool-mcp.dll：" + serverPath);
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = string.IsNullOrWhiteSpace(dotnetHost) ? "dotnet" : dotnetHost,
            Arguments = new[] { serverPath, "--project", projectPath },
            WorkingDirectory = projectPath,
            ShutdownTimeout = TimeSpan.FromSeconds(10),
            StandardErrorLines = line => stderr.WriteLine(line)
        });
        return await McpClient.CreateAsync(transport);
    }
}
