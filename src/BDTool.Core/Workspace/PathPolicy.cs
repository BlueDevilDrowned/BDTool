using System;
using System.Collections.Generic;
using System.IO;

namespace BDTool.Core.Workspace;

/// <summary>路径策略拒绝的原因，供 Provider 映射为稳定的公共错误码。</summary>
public enum PathPolicyViolation
{
    InvalidPath,
    OutsideProjectRoot,
    ReparsePoint
}

/// <summary>路径策略拒绝。校验错误必须发生在任何文件写入之前。</summary>
public sealed class PathPolicyException : Exception
{
    public PathPolicyException(PathPolicyViolation violation, string message)
        : base(message)
    {
        Violation = violation;
    }

    public PathPolicyViolation Violation { get; }
}

/// <summary>
/// Local Workspace 共用的路径约束。这里只做规范化与越界防护，不实现读取、搜索或写入业务。
/// 所有项目内路径都必须先经过本类，避免 Provider 各自拼接路径导致越界规则不一致。
/// </summary>
public static class PathPolicy
{
    private static readonly char[] s_invalidFileNameCharacters = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    private static readonly HashSet<string> s_reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
        "CONIN$", "CONOUT$", "CLOCK$"
    };

    /// <summary>Windows 下项目内路径比较必须忽略大小写。</summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>把项目根规范化为绝对路径，并去掉非根目录末尾的分隔符。</summary>
    public static string NormalizeProjectRoot(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new PathPolicyException(PathPolicyViolation.InvalidPath, "项目路径不能为空。");
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PathPolicyException(PathPolicyViolation.InvalidPath, "项目路径不是合法绝对路径。");
        }
    }

    /// <summary>
    /// 规范化项目内相对路径。返回值统一使用正斜杠，且不包含空段、点段、盘符、UNC 或 ADS。
    /// </summary>
    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw Invalid("相对路径不能为空。");
        }

        if (relativePath.IndexOf('\0') >= 0)
        {
            throw Invalid("相对路径不能包含 NUL。");
        }

        if (relativePath.IndexOf(':') >= 0)
        {
            throw Invalid("相对路径不能包含盘符或备用数据流分隔符。");
        }

        if (Path.IsPathRooted(relativePath)
            || relativePath[0] == '/'
            || relativePath[0] == '\\')
        {
            throw Invalid("相对路径不能是根路径、盘符路径或 UNC 路径。");
        }

        var segments = relativePath.Replace('\\', '/').Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                throw Invalid("相对路径不能包含空段。");
            }

            if (segment == "." || segment == "..")
            {
                throw Invalid("相对路径不能包含 . 或 .. 段。");
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                throw Invalid("相对路径段不能以点或空格结尾。");
            }

            foreach (var character in segment)
            {
                if (character < 32 || Array.IndexOf(s_invalidFileNameCharacters, character) >= 0)
                {
                    throw Invalid("相对路径包含 Windows 不允许的字符。");
                }
            }

            // Windows 在判断设备名时会忽略扩展名及其前面的尾随点/空格。
            var baseName = segment.Split('.')[0].TrimEnd(' ', '.');
            if (s_reservedNames.Contains(baseName))
            {
                throw Invalid("相对路径使用了 Windows 保留设备名。");
            }
        }

        return string.Join("/", segments);
    }

    /// <summary>
    /// 规范化根与相对路径并返回绝对路径。返回前再次检查根前缀，形成第二道越界防线。
    /// </summary>
    public static string ResolveRelativePath(string projectRoot, string relativePath)
    {
        var root = NormalizeProjectRoot(projectRoot);
        var normalized = NormalizeRelativePath(relativePath);
        var nativeRelativePath = normalized.Replace('/', Path.DirectorySeparatorChar);
        var absolutePath = Path.GetFullPath(Path.Combine(root, nativeRelativePath));

        if (!IsWithinProjectRoot(root, absolutePath))
        {
            throw new PathPolicyException(PathPolicyViolation.OutsideProjectRoot, "路径超出项目根目录。");
        }

        return absolutePath;
    }

    /// <summary>判断绝对路径是否位于项目根之下；根目录自身不算项目内文件。</summary>
    public static bool IsWithinProjectRoot(string projectRoot, string absolutePath)
    {
        var root = Path.TrimEndingDirectorySeparator(projectRoot);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return absolutePath.StartsWith(rootPrefix, PathComparison);
    }

    /// <summary>
    /// 检查单个路径是否为 reparse point。缺失路径返回 false，由调用方决定 NOT_FOUND 语义。
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 从项目根逐段检查到目标，拒绝任何已存在的 reparse point。缺失的尾部路径允许通过，
    /// 但检查与后续打开之间仍存在无法完全消除的修复窗口。
    /// </summary>
    public static void EnsureNoReparsePoint(string projectRoot, string absolutePath)
    {
        var root = NormalizeProjectRoot(projectRoot);
        if (!IsWithinProjectRoot(root, absolutePath))
        {
            throw new PathPolicyException(PathPolicyViolation.OutsideProjectRoot, "路径超出项目根目录。");
        }

        var current = root;
        if (IsReparsePoint(current))
        {
            throw Reparse();
        }

        var rootPrefixLength = root.EndsWith(Path.DirectorySeparatorChar)
            ? root.Length
            : root.Length + 1;
        var remainder = absolutePath.Substring(rootPrefixLength);
        foreach (var segment in remainder.Split(Path.DirectorySeparatorChar))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            if (IsReparsePoint(current))
            {
                throw Reparse();
            }
        }
    }

    private static PathPolicyException Invalid(string message)
    {
        return new PathPolicyException(PathPolicyViolation.InvalidPath, message);
    }

    private static PathPolicyException Reparse()
    {
        return new PathPolicyException(PathPolicyViolation.ReparsePoint, "路径包含 reparse point。");
    }
}
