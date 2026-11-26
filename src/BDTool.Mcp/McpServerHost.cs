using BDTool.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BDTool.Mcp;

/// <summary>
/// stdio MCP Host 的组合入口。它只装配 Application Gateway、目录与固定 Resource，
/// 不注册 Provider 工具，也不允许客户端通过工具名调用低层编辑器命令。
/// </summary>
internal static class McpServerHost
{
    public static async Task RunAsync(
        McpCommandLineOptions command,
        CancellationToken cancellationToken)
    {
        var runtime = BDToolComposition.Create(new BDToolCompositionOptions
        {
            ProjectPath = command.ProjectPath,
            CatalogPath = command.CatalogPath
        });
        var invoker = new McpOperationInvoker(runtime.Gateway, runtime.ProjectPath);
        var resources = new McpResources(runtime.Catalog, invoker);

        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        // stdio 的 stdout 只能承载 MCP 协议帧；诊断日志交给宿主 stderr。
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "BDTool",
                    Version = new BDToolCompositionOptions().AppVersion
                };
                options.ServerInstructions =
                    "BDTool exposes only catalog operations. Use operation IDs as tool names and pass the catalog input schema directly.";
            })
            .WithStdioServerTransport()
            .WithListToolsHandler((_, _) => ValueTask.FromResult(McpToolCatalog.CreateList(runtime.Catalog)))
            .WithCallToolHandler(async (request, token) =>
            {
                var parameters = request.Params
                    ?? throw new ArgumentException("MCP 工具调用缺少参数。");
                return await invoker.InvokeAsync(parameters, token).ConfigureAwait(false);
            })
            .WithListResourcesHandler((_, _) => ValueTask.FromResult(McpResources.List()))
            .WithReadResourceHandler((request, token) =>
            {
                var uri = request.Params?.Uri
                    ?? throw new ArgumentException("MCP Resource 请求缺少 URI。");
                return new ValueTask<ReadResourceResult>(resources.ReadAsync(uri, token));
            });

        using var host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
