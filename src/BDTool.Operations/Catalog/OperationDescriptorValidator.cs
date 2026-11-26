using System;
using System.Collections.Generic;
using System.Text.Json;
using BDTool.Contracts.Operations;
using BDTool.Core.Operations;

namespace BDTool.Operations.Catalog;

/// <summary>
/// 单个操作描述的结构与语义校验。规则集中在这里，目录加载与测试共享同一实现。
/// </summary>
public static class OperationDescriptorValidator
{
    /// <summary>允许的最短与最长超时时间。</summary>
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 3600;

    /// <summary>校验描述，返回全部错误而不是遇到第一条就退出。</summary>
    public static IReadOnlyList<string> Validate(OperationDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var errors = new List<string>();

        if (!OperationIdRules.IsValid(descriptor.Id, out var idError))
        {
            errors.Add($"id 非法：{idError}");
        }

        if (string.IsNullOrWhiteSpace(descriptor.Description))
        {
            errors.Add("description 不能为空。");
        }

        if (!OperationIdRules.IsKnownEffect(descriptor.Effect))
        {
            errors.Add($"effect 不在冻结集合中：{descriptor.Effect}");
        }

        if (descriptor.Environment.Count == 0)
        {
            errors.Add("environment 至少需要一个执行环境。");
        }

        foreach (var environment in descriptor.Environment)
        {
            if (!OperationIdRules.IsKnownEnvironment(environment))
            {
                errors.Add($"environment 含有未知值：{environment}");
            }
        }

        foreach (var capability in descriptor.RequiredCapabilities)
        {
            if (!OperationIdRules.IsValid(capability, out var capabilityError))
            {
                errors.Add($"requiredCapabilities 含非法能力 ID：{capability}（{capabilityError}）");
            }
        }

        ValidateSchema("inputSchema", descriptor.InputSchemaJson, errors);
        ValidateSchema("resultSchema", descriptor.ResultSchemaJson, errors);

        if (descriptor.TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            errors.Add($"timeoutSeconds 必须在 {MinTimeoutSeconds} 到 {MaxTimeoutSeconds} 之间。");
        }

        foreach (var artifact in descriptor.Artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact))
            {
                errors.Add("artifacts 不能包含空值。");
            }
        }

        if (string.IsNullOrWhiteSpace(descriptor.Milestone))
        {
            errors.Add("milestone 不能为空。");
        }

        if (!OperationIdRules.IsKnownAvailability(descriptor.Availability))
        {
            errors.Add($"availability 不在冻结集合中：{descriptor.Availability}");
        }
        else
        {
            // implemented 与 availability 必须双向一致，防止把未实现操作声称为可用。
            var claimedAvailable = string.Equals(
                descriptor.Availability,
                OperationAvailability.Available,
                StringComparison.Ordinal);
            if (descriptor.Implemented != claimedAvailable)
            {
                errors.Add("implemented 与 availability=available 必须一致。");
            }
        }

        return errors;
    }

    private static void ValidateSchema(string fieldName, string schemaJson, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            errors.Add($"{fieldName} 不能为空。");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{fieldName} 必须是 JSON 对象。");
            }
        }
        catch (JsonException exception)
        {
            errors.Add($"{fieldName} 不是合法 JSON：{exception.Message}");
        }
    }
}
