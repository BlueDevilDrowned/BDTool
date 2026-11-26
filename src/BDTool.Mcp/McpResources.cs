using System.Text.Json;
using BDTool.Contracts;
using BDTool.Contracts.Operations;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace BDTool.Mcp;

/// <summary>
/// MCP Resource 只提供公共目录与项目上下文。目录不触达 Provider；
/// 项目上下文仍通过 Application Gateway 执行并保留 Activity。
/// </summary>
internal sealed class McpResources
{
    public const string CatalogUri = "bdtool://catalog/operations";
    public const string ProjectContextUri = "bdtool://project/context";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IOperationCatalog _catalog;
    private readonly McpOperationInvoker _invoker;

    public McpResources(IOperationCatalog catalog, McpOperationInvoker invoker)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
    }

    public static ListResourcesResult List()
    {
        return new ListResourcesResult
        {
            Resources = new List<Resource>
            {
                new()
                {
                    Name = "BDTool operation catalog",
                    Uri = CatalogUri,
                    Description = "当前构建公开的操作目录、能力、Schema 与可用性。",
                    MimeType = "application/json"
                },
                new()
                {
                    Name = "BDTool project context",
                    Uri = ProjectContextUri,
                    Description = "当前绑定项目的 Unity 版本、Editor 连接与 Provider 能力快照。",
                    MimeType = "application/json"
                }
            }
        };
    }

    public async Task<ReadResourceResult> ReadAsync(string uri, CancellationToken cancellationToken)
    {
        string text;
        if (string.Equals(uri, CatalogUri, StringComparison.Ordinal))
        {
            var payload = new
            {
                contractVersion = BDToolContract.Version,
                operations = _catalog.GetAll()
            };
            text = JsonSerializer.Serialize(payload, s_jsonOptions);
        }
        else if (string.Equals(uri, ProjectContextUri, StringComparison.Ordinal))
        {
            var result = await _invoker.ExecuteAsync("project.context", "{}", cancellationToken)
                .ConfigureAwait(false);
            text = BDTool.Application.Serialization.OperationResultJsonSerializer.Serialize(result);
        }
        else
        {
            throw new McpException("未知 MCP Resource：" + uri);
        }

        return new ReadResourceResult
        {
            Contents = new List<ResourceContents>
            {
                new TextResourceContents
                {
                    Uri = uri,
                    MimeType = "application/json",
                    Text = text
                }
            }
        };
    }
}
