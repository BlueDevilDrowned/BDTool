using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Formation;

namespace BDTool.Orchestration;

/// <summary>
/// requestId 幂等账本。命令身份由 command、entityId 和完整请求载荷共同决定；
/// 同一 requestId 携带不同载荷必须失败，不能把旧结果伪装成新请求成功。
/// </summary>
internal static class FormationRequestLedger
{
    /// <summary>
    /// 计算排除顶层 requestId 后的规范化 JSON SHA-256。属性按 ordinal 排序，
    /// 不输出空白，确保相同语义请求不受序列化器属性顺序影响。
    /// </summary>
    public static string ComputePayloadSha256(object request)
    {
        var element = JsonSerializer.SerializeToElement(request);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, element, skipTopLevelRequestId: true);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    /// <summary>
    /// 返回 true 表示完全相同请求已执行；同 requestId 的不同命令或载荷抛出冲突。
    /// </summary>
    public static bool HasMatchingReceipt(
        FormationData formation,
        string requestId,
        string command,
        string entityId,
        string payloadSha256)
    {
        if (!formation.RequestReceipts.TryGetValue(requestId, out var receipt))
        {
            return false;
        }

        if (!string.Equals(receipt.Command, command, StringComparison.Ordinal)
            || !string.Equals(receipt.EntityId, entityId, StringComparison.Ordinal)
            || !string.Equals(
                receipt.PayloadSha256,
                payloadSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Conflict(
                "requestId 已用于不同的 Formation 命令或请求载荷。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["requestId"] = requestId,
                    ["expectedCommand"] = receipt.Command,
                    ["actualCommand"] = command,
                    ["expectedEntityId"] = receipt.EntityId,
                    ["actualEntityId"] = entityId,
                    ["expectedPayloadSha256"] = receipt.PayloadSha256,
                    ["actualPayloadSha256"] = payloadSha256
                });
        }

        return true;
    }

    public static void EnsureUniqueRequestId(FormationData formation, string requestId)
    {
        if (formation.RequestReceipts.ContainsKey(requestId))
        {
            throw Conflict(
                "requestId 已存在。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["requestId"] = requestId
                });
        }
    }

    public static void AddReceipt(
        FormationData formation,
        string requestId,
        string command,
        string entityId,
        string payloadSha256,
        DateTimeOffset now)
    {
        formation.RequestReceipts[requestId] = new FormationRequestReceiptData
        {
            RequestId = requestId,
            Command = command,
            EntityId = entityId,
            PayloadSha256 = payloadSha256,
            RecordedAt = now
        };
    }

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement element,
        bool skipTopLevelRequestId)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (skipTopLevelRequestId
                        && string.Equals(property.Name, "requestId", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, skipTopLevelRequestId: false);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item, skipTopLevelRequestId: false);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static FormationServiceException Conflict(
        string message,
        IReadOnlyDictionary<string, string> details) =>
        new(ErrorCodes.PreconditionFailed, message, details);
}
