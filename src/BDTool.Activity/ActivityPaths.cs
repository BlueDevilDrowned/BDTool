using System;
using System.Globalization;
using System.IO;
using BDTool.Core.Projects;

namespace BDTool.Activity;

/// <summary>
/// Activity 的唯一权威路径构造器。任何需要读取或写入 Activity 的组件都必须
/// 通过这里取路径，避免出现第二套目录规则。
/// </summary>
public static class ActivityPaths
{
    /// <summary>Activity 文件扩展名。</summary>
    public const string FileExtension = ".json";

    /// <summary>ActivityId 允许的最大长度。</summary>
    public const int MaxActivityIdLength = 64;

    /// <summary>
    /// 返回某一天（UTC）的 Activity 目录：&lt;project&gt;/.bdtool/state/activity/&lt;yyyyMMdd&gt;。
    /// 时间戳带偏移时先转换为 UTC，确保跨时区写入同一自然日。
    /// </summary>
    public static string GetActivityDirectory(string projectPath, DateTimeOffset timestamp)
    {
        return Path.Combine(ProjectLayout.GetActivityRoot(projectPath), FormatUtcDay(timestamp));
    }

    /// <summary>格式化 UTC 目录名。</summary>
    public static string FormatUtcDay(DateTimeOffset timestamp)
    {
        return timestamp.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 返回单个 Activity 文件路径。ActivityId 必须在写入前通过
    /// <see cref="IsValidActivityId"/> 校验，防止路径穿越或非法文件名。
    /// </summary>
    public static string GetActivityFilePath(string projectPath, string activityId, DateTimeOffset timestamp)
    {
        if (!IsValidActivityId(activityId, out var error))
        {
            throw new ArgumentException(error, nameof(activityId));
        }

        var directory = GetActivityDirectory(projectPath, timestamp);
        var fileName = activityId + FileExtension;
        var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));
        var root = Path.GetFullPath(ProjectLayout.GetActivityRoot(projectPath));

        // 即使 ActivityId 校验通过，也再验证一次最终路径没有越出 Activity 根目录。
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Activity 路径越出活动根目录。", nameof(activityId));
        }

        return fullPath;
    }

    /// <summary>
    /// 校验 ActivityId：只允许字母、数字、连字符和下划线，长度 1-64。
    /// 不允许点、空格和路径分隔符，保证可以安全地作为文件名。
    /// </summary>
    public static bool IsValidActivityId(string? activityId, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(activityId))
        {
            error = "ActivityId 不能为空。";
            return false;
        }

        if (activityId.Length > MaxActivityIdLength)
        {
            error = $"ActivityId 超过 {MaxActivityIdLength} 个字符。";
            return false;
        }

        foreach (var character in activityId)
        {
            var allowed = character is >= 'a' and <= 'z'
                || character is >= 'A' and <= 'Z'
                || character is >= '0' and <= '9'
                || character == '-'
                || character == '_';
            if (!allowed)
            {
                error = "ActivityId 只允许字母、数字、连字符和下划线。";
                return false;
            }
        }

        return true;
    }
}
