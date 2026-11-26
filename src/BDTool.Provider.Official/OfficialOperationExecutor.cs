using System.Text.Json;
using BDTool.Contracts;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;

namespace BDTool.Provider.Official;

/// <summary>
/// 将公开 Editor 操作映射为官网 Pipeline 命令。Provider 主体只管理状态与 Job，
/// 操作投影集中在这里，避免把协议命令和生命周期混成一份巨型实现。
/// </summary>
internal sealed class OfficialOperationExecutor
{
    private readonly IOfficialPipelineClient _client;

    public OfficialOperationExecutor(IOfficialPipelineClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public void ValidateInput(EditorOperationRequest request)
    {
        using var document = ParseInput(request.InputJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("输入必须是 JSON 对象。");
        }

        if (request.OperationId == "project.asset-snapshot")
        {
            OfficialPrefabScripts.BuildSnapshotCode(RequireString(root, "assetPath"));
        }
        else if (request.OperationId == "project.prefab-set-active")
        {
            OfficialPrefabScripts.BuildSetActiveCode(
                RequireString(root, "assetPath"),
                RequireString(root, "expectedSha256"),
                RequireBoolean(root, "isActive"),
                request.DryRun || GetBoolean(root, "dryRun", defaultValue: false));
        }
    }

    public async Task<string> ExecuteAsync(
        EditorOperationRequest request,
        EditorStatus status,
        string projectPath,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        using var document = ParseInput(request.InputJson);
        var input = document.RootElement;
        switch (request.OperationId)
        {
            case "project.inspect":
                var hierarchy = await _client.ExecuteAsync(
                        projectPath,
                        "scene get_hierarchy",
                        new Dictionary<string, object?>(StringComparer.Ordinal),
                        timeoutMilliseconds,
                        cancellationToken)
                    .ConfigureAwait(false);
                return OfficialHierarchyNormalizer.Normalize(hierarchy, status.UnityVersion, input);

            case "project.asset-snapshot":
                return await ExecuteScriptAsync(
                        projectPath,
                        OfficialPrefabScripts.BuildSnapshotCode(RequireString(input, "assetPath")),
                        timeoutMilliseconds,
                        cancellationToken)
                    .ConfigureAwait(false);

            case "project.prefab-set-active":
                return await ExecuteScriptAsync(
                        projectPath,
                        OfficialPrefabScripts.BuildSetActiveCode(
                            RequireString(input, "assetPath"),
                            RequireString(input, "expectedSha256"),
                            RequireBoolean(input, "isActive"),
                            request.DryRun || GetBoolean(input, "dryRun", defaultValue: false)),
                        timeoutMilliseconds,
                        cancellationToken)
                    .ConfigureAwait(false);

            default:
                throw new EditorException(
                    ErrorCodes.CapabilityUnsupported,
                    "Official Provider 不支持该 Editor 操作。");
        }
    }

    private async Task<string> ExecuteScriptAsync(
        string projectPath,
        string code,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var result = await _client.ExecuteAsync(
                projectPath,
                "script eval",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = code
                },
                timeoutMilliseconds,
                cancellationToken)
            .ConfigureAwait(false);
        OfficialPrefabScripts.EnsureSuccessfulResult(result);
        return result.GetRawText();
    }

    private static JsonDocument ParseInput(string? inputJson) =>
        JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);

    private static string RequireString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException("字段 " + propertyName + " 必须是非空字符串。");
        }

        return value.GetString()!;
    }

    private static bool RequireBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JsonException("字段 " + propertyName + " 必须是布尔值。");
        }

        return value.GetBoolean();
    }

    private static bool GetBoolean(JsonElement root, string propertyName, bool defaultValue) =>
        root.TryGetProperty(propertyName, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;
}
