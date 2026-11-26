using System.Text;
using System.Text.Json;
using BDTool.Contracts.Errors;

namespace BDTool.Provider.Official;

/// <summary>
/// 把官方 `scene get_hierarchy` 的全量结果收敛为 BDTool ProjectInspectHandler
/// 已冻结的上游形状，并在 Provider 边界执行 rootPath、深度和节点预算。
/// </summary>
internal static class OfficialHierarchyNormalizer
{
    public static string Normalize(
        JsonElement source,
        string unityVersion,
        JsonElement input)
    {
        var rootPath = GetString(input, "rootPath");
        var includeComponents = GetBoolean(input, "includeComponents", defaultValue: false);
        var includeInactive = GetBoolean(input, "includeInactive", defaultValue: true);
        var maxDepth = Math.Clamp(GetInt32(input, "maxDepth", 8), 0, 32);
        var maxObjects = Math.Clamp(GetInt32(input, "maxObjects", 2000), 1, 10000);

        JsonElement? selectedNode = null;
        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            selectedNode = FindNode(source, NormalizeHierarchyPath(rootPath))
                ?? throw new OfficialPipelineException(
                    ErrorCodes.NotFound,
                    "官方 Pipeline 层级中不存在 rootPath：" + rootPath);
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            var sceneName = GetString(source, "sceneName");
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                var scenePath = GetString(source, "scenePath");
                sceneName = Path.GetFileNameWithoutExtension(scenePath);
            }

            writer.WriteString("unityVersion", unityVersion);
            writer.WriteString("sceneName", sceneName);
            writer.WriteBoolean("isPrefabMode", false);
            writer.WriteStartArray("hierarchy");

            var remaining = maxObjects;
            if (selectedNode is { } selected)
            {
                if (remaining > 0 && ShouldInclude(selected, includeInactive))
                {
                    WriteNode(
                        writer,
                        selected,
                        depth: 0,
                        maxDepth,
                        includeComponents,
                        includeInactive,
                        ref remaining);
                }
            }
            else if (source.ValueKind == JsonValueKind.Object
                && source.TryGetProperty("roots", out var roots)
                && roots.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in roots.EnumerateArray())
                {
                    if (remaining <= 0 || !ShouldInclude(node, includeInactive))
                    {
                        continue;
                    }

                    WriteNode(
                        writer,
                        node,
                        depth: 0,
                        maxDepth,
                        includeComponents,
                        includeInactive,
                        ref remaining);
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteNode(
        Utf8JsonWriter writer,
        JsonElement node,
        int depth,
        int maxDepth,
        bool includeComponents,
        bool includeInactive,
        ref int remaining)
    {
        if (remaining <= 0)
        {
            return;
        }

        remaining--;
        writer.WriteStartObject();
        writer.WriteString("name", GetString(node, "name"));
        writer.WriteString(
            "path",
            GetString(node, "hierarchyPath"));
        writer.WriteBoolean("isActive", GetBoolean(node, "activeSelf", defaultValue: false));

        if (includeComponents)
        {
            writer.WriteStartArray("components");
            if (node.TryGetProperty("components", out var components)
                && components.ValueKind == JsonValueKind.Array)
            {
                foreach (var component in components.EnumerateArray())
                {
                    if (component.ValueKind == JsonValueKind.String)
                    {
                        writer.WriteStringValue(component.GetString());
                    }
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteStartArray("children");
        if (depth < maxDepth
            && remaining > 0
            && node.TryGetProperty("children", out var children)
            && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                if (remaining <= 0)
                {
                    break;
                }

                if (ShouldInclude(child, includeInactive))
                {
                    WriteNode(
                        writer,
                        child,
                        depth + 1,
                        maxDepth,
                        includeComponents,
                        includeInactive,
                        ref remaining);
                }
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static JsonElement? FindNode(JsonElement scope, string hierarchyPath)
    {
        if (scope.ValueKind == JsonValueKind.Object
            && string.Equals(GetString(scope, "hierarchyPath"), hierarchyPath, StringComparison.Ordinal))
        {
            return scope;
        }

        foreach (var childCollectionName in new[] { "roots", "children" })
        {
            if (!scope.TryGetProperty(childCollectionName, out var children)
                || children.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var child in children.EnumerateArray())
            {
                var match = FindNode(child, hierarchyPath);
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return null;
    }

    private static string NormalizeHierarchyPath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        return normalized.StartsWith("/", StringComparison.Ordinal)
            ? normalized
            : "/" + normalized;
    }

    private static bool ShouldInclude(JsonElement node, bool includeInactive) =>
        includeInactive || GetBoolean(node, "activeSelf", defaultValue: false);

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool GetBoolean(JsonElement element, string name, bool defaultValue) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    private static int GetInt32(JsonElement element, string name, int defaultValue) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var result)
            ? result
            : defaultValue;
}
