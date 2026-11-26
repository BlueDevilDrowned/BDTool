using BDTool.Contracts;
using BDTool.Composition;

namespace BDTool.Mcp;

/// <summary>
/// bdtool-mcp 的可测试入口。默认运行 stdio 服务；config 子命令只打印配置。
/// 未知参数返回退出码 2，启动或协议运行失败由 Program 边界映射为非零。
/// </summary>
public static class McpEntrypoint
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var command = McpCommandLine.Parse(args);
        if (command.Kind == McpCommandKind.Invalid)
        {
            stderr.WriteLine("bdtool-mcp: " + command.Error);
            return 2;
        }

        if (command.Kind == McpCommandKind.Help)
        {
            WriteUsage(stdout);
            return 0;
        }

        if (command.Kind == McpCommandKind.Version)
        {
            var version = new BDToolCompositionOptions().AppVersion;
            stdout.WriteLine("bdtool-mcp " + version + " (contract " + BDToolContract.Version + ")");
            return 0;
        }

        if (command.Kind == McpCommandKind.Config)
        {
            try
            {
                var projectPath = string.IsNullOrWhiteSpace(command.ProjectPath)
                    ? Directory.GetCurrentDirectory()
                    : command.ProjectPath;
                stdout.WriteLine(McpConfigurationGenerator.Generate(
                    command.Format,
                    projectPath,
                    typeof(McpEntrypoint).Assembly.Location));
                return 0;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                stderr.WriteLine("bdtool-mcp: 配置生成失败：" + exception.Message);
                return 2;
            }
        }

        try
        {
            await McpServerHost.RunAsync(command, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            // stdout 已由 stdio transport 占用，启动失败只能写 stderr。
            stderr.WriteLine("bdtool-mcp: " + exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("bdtool-mcp " + new BDToolCompositionOptions().AppVersion);
        writer.WriteLine();
        writer.WriteLine("用法:");
        writer.WriteLine("  bdtool-mcp [--project <path>] [--catalog <file>]");
        writer.WriteLine("  bdtool-mcp serve [同上]");
        writer.WriteLine("  bdtool-mcp config [--project <path>] [--format json|codex]");
        writer.WriteLine("  bdtool-mcp --version | --help");
    }
}
