using System;
using System.Collections.Generic;

namespace BDTool.Core.Skills;

/// <summary>
/// .bdtool-managed 的纯文本格式。标记只记录格式版本、Skill 名称与源树校验和，
/// 不执行反序列化对象绑定，避免把项目内文件当成可执行配置。
/// </summary>
internal static class ManagedSkillMarker
{
    public const string FileName = ".bdtool-managed";
    public const string FormatHeader = "bdtool-managed-skill";
    public const string CurrentVersion = "1";

    /// <summary>生成稳定、可人工检查的标记文本；末尾换行便于文本工具读取。</summary>
    public static string Create(string skillName, string checksum)
    {
        return string.Join(
            "\n",
            FormatHeader,
            "version=" + CurrentVersion,
            "name=" + skillName,
            "checksum=" + checksum,
            string.Empty);
    }

    /// <summary>
    /// 严格验证标记的格式、版本、名称与校验和形态。只有完整匹配的标记才允许覆盖目标目录，
    /// 从而避免把仅同名的用户目录误判为受管目录。
    /// </summary>
    public static bool TryParse(string content, string expectedSkillName, out string checksum)
    {
        checksum = string.Empty;
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length < 2 || !string.Equals(lines[0], FormatHeader, StringComparison.Ordinal))
        {
            return false;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                return false;
            }

            var key = line.Substring(0, separator);
            var value = line.Substring(separator + 1);
            if (fields.ContainsKey(key))
            {
                return false;
            }

            fields.Add(key, value);
        }

        if (!fields.TryGetValue("version", out var version)
            || !string.Equals(version, CurrentVersion, StringComparison.Ordinal))
        {
            return false;
        }

        if (!fields.TryGetValue("name", out var name)
            || !string.Equals(name, expectedSkillName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!fields.TryGetValue("checksum", out var parsedChecksum)
            || !IsSha256Hex(parsedChecksum))
        {
            return false;
        }

        checksum = parsedChecksum;
        return true;
    }

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isDigit = character >= '0' && character <= '9';
            var isLowerHex = character >= 'a' && character <= 'f';
            if (!isDigit && !isLowerHex)
            {
                return false;
            }
        }

        return true;
    }
}
