using System.Text.Json;
using BDTool.Application.Serialization;
using BDTool.Contracts;
using BDTool.Contracts.Activity;
using BDTool.Contracts.Gateway;
using BDTool.Contracts.Operations;
using ModelContextProtocol.Protocol;

namespace BDTool.Mcp;

/// <summary>
/// MCP 到 Application 的唯一工具调用边界。参数对象是操作输入本身，
/// 不是带命令或 Provider 类型的外壳；审计来源固定为 mcp。
/// </summary>
internal sealed class McpOperationInvoker
{
    private static readonly JsonSerializerOptions s_inputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IApplicationGateway _gateway;
    private readonly string _projectPath;

    public McpOperationInvoker(IApplicationGateway gateway, string projectPath)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _projectPath = projectPath ?? throw new ArgumentNullException(nameof(projectPath));
    }

    public async ValueTask<CallToolResult> InvokeAsync(
        CallToolRequestParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (string.IsNullOrWhiteSpace(parameters.Name))
        {
            throw new ArgumentException("MCP 工具名不能为空。", nameof(parameters));
        }

        var inputJson = parameters.Arguments is null
            ? "{}"
            : JsonSerializer.Serialize(parameters.Arguments, s_inputOptions);
        var result = await ExecuteAsync(parameters.Name, inputJson, cancellationToken).ConfigureAwait(false);
        var text = OperationResultJsonSerializer.Serialize(result);
        return new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = text }
            },
            StructuredContent = OperationResultJsonSerializer.SerializeToElement(result),
            IsError = result.Status != OperationStatus.Completed
        };
    }

    public async Task<OperationResult> ExecuteAsync(
        string operationId,
        string inputJson,
        CancellationToken cancellationToken)
    {
        var activityId = Guid.NewGuid().ToString("n");
        var request = new OperationRequest
        {
            OperationId = operationId,
            ProjectPath = _projectPath,
            InputJson = string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson
        };
        var activity = new ActivityContext
        {
            ActivityId = activityId,
            TraceId = activityId,
            Source = ActivitySources.Mcp,
            Actor = new ActivityActor
            {
                Type = ActorTypes.Ai,
                AgentId = string.Empty,
                SessionId = string.Empty
            }
        };

        return await _gateway.ExecuteAsync(request, activity, cancellationToken).ConfigureAwait(false);
    }
}
