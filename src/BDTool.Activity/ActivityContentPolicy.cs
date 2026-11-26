using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BDTool.Core.Activity;

namespace BDTool.Activity;

/// <summary>
/// 写入 Activity 前的输入/输出摘要结果。
/// PreviewJson 始终是合法 JSON；大内容只保留哈希与大小，不内联原值。
/// </summary>
public sealed class ActivityPayloadSummary
{
    /// <summary>可直接写入 ActivityRecord 的预览 JSON。</summary>
    public string PreviewJson { get; init; } = "{}";

    /// <summary>是否检测到并按策略替换了敏感字段。</summary>
    public bool Redacted { get; init; }

    /// <summary>预览是否因长度限制被截断。</summary>
    public bool Truncated { get; init; }

    /// <summary>原始内容是否超过大内容阈值，需要转存 artifact。</summary>
    public bool ArtifactRequired { get; init; }

    /// <summary>原始 UTF-8 字节数。</summary>
    public long OriginalBytes { get; init; }
}

/// <summary>
/// Activity 内容策略实现：敏感字段只保留存在性与长度，大内容只保留摘要，
/// 预览长度有上限。策略本身是纯函数，便于测试和后续 Activity 存储复用。
/// </summary>
public static class ActivityContentPolicy
{
    /// <summary>对一段输入或输出生成可写入 Activity 的摘要。</summary>
    public static ActivityPayloadSummary Summarize(string? payload, string payloadKind)
    {
        var text = payload ?? string.Empty;
        if (text.Length == 0)
        {
            return new ActivityPayloadSummary();
        }

        var bytes = Encoding.UTF8.GetByteCount(text);
        var redacted = false;
        var redactedNode = TryRedactJson(text, ref redacted);
        var normalized = redactedNode?.ToJsonString() ?? text;
        var artifactRequired = bytes > ActivityPolicy.LargeContentThresholdBytes;

        if (artifactRequired)
        {
            // 大内容不内联，只保留足以核对的摘要信息。
            var summary = new Dictionary<string, object?>
            {
                ["$artifact"] = "required",
                ["kind"] = payloadKind,
                ["bytes"] = bytes,
                ["sha256"] = ComputeSha256(text)
            };
            return new ActivityPayloadSummary
            {
                PreviewJson = JsonSerializer.Serialize(summary),
                Redacted = redacted,
                ArtifactRequired = true,
                OriginalBytes = bytes
            };
        }

        var truncated = normalized.Length > ActivityPolicy.PreviewMaxCharacters;
        if (truncated)
        {
            var truncatedPreview = new Dictionary<string, object?>
            {
                ["$truncated"] = true,
                ["kind"] = payloadKind,
                ["characters"] = normalized.Length,
                ["prefix"] = normalized.Substring(0, ActivityPolicy.PreviewMaxCharacters)
            };
            return new ActivityPayloadSummary
            {
                PreviewJson = JsonSerializer.Serialize(truncatedPreview),
                Redacted = redacted,
                Truncated = true,
                OriginalBytes = bytes
            };
        }

        // 非 JSON 文本序列化为 JSON 字符串，保证 PreviewJson 可用于结构化存储。
        var preview = redactedNode is null ? JsonSerializer.Serialize(normalized) : normalized;
        return new ActivityPayloadSummary
        {
            PreviewJson = preview,
            Redacted = redacted,
            OriginalBytes = bytes
        };
    }

    private static JsonNode? TryRedactJson(string text, ref bool redacted)
    {
        try
        {
            var node = JsonNode.Parse(text);
            return node is null ? null : RedactNode(node, ref redacted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? RedactNode(JsonNode? node, ref bool redacted)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject jsonObject:
                {
                    var result = new JsonObject();
                    foreach (var property in jsonObject)
                    {
                        if (ActivityPolicy.IsSensitiveField(property.Key))
                        {
                            redacted = true;
                            var rawLength = property.Value?.ToJsonString().Length ?? 0;
                            result[property.Key] = new JsonObject
                            {
                                ["$redacted"] = true,
                                ["$length"] = rawLength
                            };
                        }
                        else
                        {
                            result[property.Key] = RedactNode(property.Value, ref redacted);
                        }
                    }

                    return result;
                }

            case JsonArray jsonArray:
                {
                    var result = new JsonArray();
                    foreach (var item in jsonArray)
                    {
                        result.Add(RedactNode(item, ref redacted));
                    }

                    return result;
                }

            default:
                return node.DeepClone();
        }
    }

    private static string ComputeSha256(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
