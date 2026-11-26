namespace BDTool.Mcp;

/// <summary>MCP 进程的入口模式。</summary>
internal enum McpCommandKind
{
    Serve,
    Config,
    Help,
    Version,
    Invalid
}

/// <summary>输出配置的目标格式。</summary>
public enum McpConfigFormat
{
    Json,
    CodexToml
}

/// <summary>解析后的 bdtool-mcp 参数；只描述入口，不包含应用运行状态。</summary>
internal sealed class McpCommandLineOptions
{
    public McpCommandKind Kind { get; set; }
    public McpConfigFormat Format { get; set; } = McpConfigFormat.Json;
    public string ProjectPath { get; set; } = string.Empty;
    public string? CatalogPath { get; set; }
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// 手工解析少量稳定参数，确保配置生成与 stdio 服务不会把未知参数交给
/// Generic Host 当作配置键而静默接受。
/// </summary>
internal static class McpCommandLine
{
    public static McpCommandLineOptions Parse(string[] args)
    {
        var result = new McpCommandLineOptions();
        if (args.Length == 0)
        {
            result.Kind = McpCommandKind.Serve;
            return result;
        }

        var index = 0;
        result.Kind = args[0] switch
        {
            "serve" => McpCommandKind.Serve,
            "config" => McpCommandKind.Config,
            "--help" or "-h" or "help" => McpCommandKind.Help,
            "--version" or "-v" or "version" => McpCommandKind.Version,
            var value when value.StartsWith("-", StringComparison.Ordinal) => McpCommandKind.Serve,
            _ => McpCommandKind.Invalid
        };

        if (result.Kind == McpCommandKind.Invalid)
        {
            result.Error = "未知命令：" + args[0];
            return result;
        }

        if (result.Kind is McpCommandKind.Serve or McpCommandKind.Config)
        {
            if (args[0] is "serve" or "config")
            {
                index = 1;
            }
        }
        else
        {
            return result;
        }

        var projectSeen = false;
        var catalogSeen = false;
        var formatSeen = false;

        for (; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--project":
                    if (projectSeen || !TryReadValue(args, ref index, out var project))
                    {
                        return Fail(result, projectSeen ? "重复选项 --project。" : "--project 需要一个路径。");
                    }

                    projectSeen = true;
                    result.ProjectPath = project;
                    break;
                case "--catalog" when result.Kind == McpCommandKind.Serve:
                    if (catalogSeen || !TryReadValue(args, ref index, out var catalog))
                    {
                        return Fail(result, catalogSeen ? "重复选项 --catalog。" : "--catalog 需要一个文件路径。");
                    }

                    catalogSeen = true;
                    result.CatalogPath = catalog;
                    break;
                case "--format" when result.Kind == McpCommandKind.Config:
                    if (formatSeen || !TryReadValue(args, ref index, out var format))
                    {
                        return Fail(result, formatSeen ? "重复选项 --format。" : "--format 需要 json 或 codex。");
                    }

                    formatSeen = true;
                    if (string.Equals(format, "json", StringComparison.Ordinal))
                    {
                        result.Format = McpConfigFormat.Json;
                    }
                    else if (string.Equals(format, "codex", StringComparison.Ordinal))
                    {
                        result.Format = McpConfigFormat.CodexToml;
                    }
                    else
                    {
                        return Fail(result, "--format 只支持 json 或 codex。");
                    }

                    break;
                default:
                    return Fail(result, "未知选项 " + argument + "。");
            }
        }

        return result;
    }

    private static McpCommandLineOptions Fail(McpCommandLineOptions options, string message)
    {
        options.Kind = McpCommandKind.Invalid;
        options.Error = message;
        return options;
    }

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        value = string.Empty;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        value = args[++index];
        return true;
    }
}
