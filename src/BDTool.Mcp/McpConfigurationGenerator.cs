using System.Text.Json;

namespace BDTool.Mcp;

/// <summary>
/// 生成仅启动 stdio MCP 服务的客户端配置。配置不包含模型、凭据或 Provider 参数，
/// 项目边界通过显式 --project 固定，避免宿主工作目录变化时绑错项目。
/// </summary>
public static class McpConfigurationGenerator
{
    public static string Generate(
        McpConfigFormat format,
        string projectPath,
        string serverAssemblyPath)
    {
        var resolvedProject = Path.GetFullPath(
            string.IsNullOrWhiteSpace(projectPath) ? Directory.GetCurrentDirectory() : projectPath);
        var resolvedAssembly = Path.GetFullPath(serverAssemblyPath);
        return format == McpConfigFormat.CodexToml
            ? GenerateCodexToml(resolvedProject, resolvedAssembly)
            : GenerateJson(resolvedProject, resolvedAssembly);
    }

    private static string GenerateJson(string projectPath, string serverAssemblyPath)
    {
        var document = new
        {
            mcpServers = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["bdtool"] = new
                {
                    command = "dotnet",
                    args = new[] { serverAssemblyPath, "--project", projectPath },
                    cwd = projectPath
                }
            }
        };

        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string GenerateCodexToml(string projectPath, string serverAssemblyPath)
    {
        return string.Join(
            "\n",
            "[mcp_servers.bdtool]",
            "command = \"dotnet\"",
            "args = [" + Quote(serverAssemblyPath) + ", \"--project\", " + Quote(projectPath) + "]",
            "cwd = " + Quote(projectPath),
            "startup_timeout_sec = 30",
            string.Empty);
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
