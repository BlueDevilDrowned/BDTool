using System;
using BDTool.Contracts.Operations;

namespace BDTool.Core.Operations;

/// <summary>
/// 操作 ID 与目录枚举值的稳定规则。规则放在 Core 中，让目录加载器和测试
/// 使用同一份实现，避免两处判定漂移。
/// </summary>
public static class OperationIdRules
{
    /// <summary>操作 ID 允许的最大长度。</summary>
    public const int MaxLength = 64;

    /// <summary>单个点分段允许的最大长度。</summary>
    public const int MaxSegmentLength = 32;

    /// <summary>
    /// 校验点分小写 ID，例如 system.doctor。每段必须以字母开头，
    /// 只允许小写字母、数字与连字符，禁止空段和连续点。
    /// </summary>
    public static bool IsValid(string? operationId, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(operationId))
        {
            error = "操作 ID 不能为空。";
            return false;
        }

        if (operationId.Length > MaxLength)
        {
            error = $"操作 ID 超过 {MaxLength} 个字符。";
            return false;
        }

        if (operationId.StartsWith('.') || operationId.EndsWith('.'))
        {
            error = "操作 ID 不能以点开头或结尾。";
            return false;
        }

        var segments = operationId.Split('.');
        if (segments.Length < 2)
        {
            error = "操作 ID 至少包含一个命名空间段和一个操作段。";
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length is 0 or > MaxSegmentLength)
            {
                error = $"操作 ID 的每个分段长度必须在 1 到 {MaxSegmentLength} 之间。";
                return false;
            }

            if (segment[0] is < 'a' or > 'z')
            {
                error = "操作 ID 的每个分段必须以小写字母开头。";
                return false;
            }

            foreach (var character in segment)
            {
                var allowed = character is >= 'a' and <= 'z'
                    || character is >= '0' and <= '9'
                    || character == '-';
                if (!allowed)
                {
                    error = "操作 ID 只允许小写字母、数字、连字符和点。";
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>判断效果是否属于冻结集合。</summary>
    public static bool IsKnownEffect(string? effect) => Contains(OperationEffects.All, effect);

    /// <summary>判断执行环境是否属于冻结集合。</summary>
    public static bool IsKnownEnvironment(string? environment) => Contains(OperationEnvironments.All, environment);

    /// <summary>判断可用性是否属于冻结集合。</summary>
    public static bool IsKnownAvailability(string? availability) => Contains(OperationAvailability.All, availability);

    private static bool Contains(System.Collections.Generic.IReadOnlyList<string> values, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (string.Equals(value, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
