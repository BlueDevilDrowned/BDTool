using System;
using System.Collections.Generic;

namespace BDTool.Core.Activity;

/// <summary>
/// Activity 存储与脱敏的冻结策略。契约层只描述数据形状，具体阈值和字段名
/// 集中在 Core，避免 CLI、UI 和后续 Activity 实现出现不一致的判定。
/// </summary>
public static class ActivityPolicy
{
    /// <summary>单次输入或输出超过该字节数时写入 artifact，Activity 只保留摘要与引用。</summary>
    public const int LargeContentThresholdBytes = 64 * 1024;

    /// <summary>Activity 文件默认保留天数。</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>项目内状态目录名。</summary>
    public const string StateDirectoryName = ".bdtool";

    /// <summary>状态目录下的相对路径片段，统一使用正斜杠描述跨平台逻辑路径。</summary>
    public const string ActivityRelativeRoot = "state/activity";

    /// <summary>日志预览的最大字符数，超出部分截断并标记。</summary>
    public const int PreviewMaxCharacters = 2048;

    private static readonly string[] s_sensitiveFieldNames =
    {
        "token",
        "password",
        "secret",
        "apikey",
        "authorization",
        "cookie",
        "sessioncredential",
        "membertoken"
    };

    /// <summary>
    /// 敏感字段名清单，统一小写且去掉分隔符后的形式。
    /// 只暴露规范化名称，防止调用方依赖大小写或下划线写法。
    /// </summary>
    public static IReadOnlyList<string> SensitiveFieldNames => s_sensitiveFieldNames;

    /// <summary>
    /// 判断字段是否必须脱敏。除精确匹配外，任何以 token/password/secret 结尾的字段
    /// 也视为敏感，覆盖 Provider 后续可能新增的同类命名。
    /// </summary>
    public static bool IsSensitiveField(string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        var normalized = Normalize(fieldName);
        foreach (var candidate in s_sensitiveFieldNames)
        {
            if (string.Equals(normalized, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return normalized.EndsWith("token", StringComparison.Ordinal)
            || normalized.EndsWith("password", StringComparison.Ordinal)
            || normalized.EndsWith("secret", StringComparison.Ordinal);
    }

    private static string Normalize(string fieldName)
    {
        var buffer = new char[fieldName.Length];
        var length = 0;
        foreach (var character in fieldName)
        {
            if (character is '_' or '-' or ' ')
            {
                continue;
            }

            buffer[length++] = char.ToLowerInvariant(character);
        }

        return new string(buffer, 0, length);
    }
}
