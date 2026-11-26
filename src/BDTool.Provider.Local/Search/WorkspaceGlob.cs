using System;
using System.IO;
using BDTool.Provider.Local.Internal;

namespace BDTool.Provider.Local.Search;

/// <summary>
/// M1 glob 匹配器。模式统一使用正斜杠；整段 <c>**</c> 跨越零到多个目录，
/// 其余段中的 <c>*</c>/<c>?</c> 只在单段内匹配。匹配使用动态规划而不是正则，
/// 便于精确表达路径段边界并避免把用户模式拼进正则造成额外解释成本。
/// </summary>
internal sealed class WorkspaceGlob
{
    private readonly string[] _segments;

    private WorkspaceGlob(string pattern, string[] segments)
    {
        Pattern = pattern;
        _segments = segments;
    }

    public string Pattern { get; }

    public static WorkspaceGlob Parse(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw WorkspaceErrors.Invalid("搜索 glob 不能为空。");
        }

        if (pattern.IndexOf('\0') >= 0)
        {
            throw WorkspaceErrors.Invalid("搜索 glob 不能包含 NUL。");
        }

        if (pattern.IndexOf('\\') >= 0)
        {
            throw WorkspaceErrors.Invalid("搜索 glob 必须使用 / 作为路径分隔符。");
        }

        if (pattern.IndexOf(':') >= 0
            || pattern[0] == '/'
            || Path.IsPathRooted(pattern))
        {
            throw WorkspaceErrors.Invalid("搜索 glob 不能是绝对路径、盘符或 UNC 路径。");
        }

        var segments = pattern.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                throw WorkspaceErrors.Invalid("搜索 glob 不能包含空段。");
            }

            if (segment == "." || segment == "..")
            {
                throw WorkspaceErrors.Invalid("搜索 glob 不能包含 . 或 .. 段。");
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                throw WorkspaceErrors.Invalid("搜索 glob 段不能以点或空格结尾。");
            }

            foreach (var character in segment)
            {
                if (character < 32)
                {
                    throw WorkspaceErrors.Invalid("搜索 glob 包含控制字符。");
                }
            }
        }

        return new WorkspaceGlob(pattern, segments);
    }

    public bool IsMatch(string relativePath)
    {
        var pathSegments = relativePath.Split('/');
        var memo = new sbyte[_segments.Length + 1, pathSegments.Length + 1];
        return MatchSegments(pathSegments, patternIndex: 0, pathIndex: 0, memo);
    }

    private bool MatchSegments(
        string[] pathSegments,
        int patternIndex,
        int pathIndex,
        sbyte[,] memo)
    {
        if (memo[patternIndex, pathIndex] != 0)
        {
            return memo[patternIndex, pathIndex] == 1;
        }

        bool matched;
        if (patternIndex == _segments.Length)
        {
            matched = pathIndex == pathSegments.Length;
        }
        else if (_segments[patternIndex] == "**")
        {
            // ** 可以匹配零个目录段，也可以消费一个段后继续尝试匹配自身。
            matched = MatchSegments(pathSegments, patternIndex + 1, pathIndex, memo)
                || (pathIndex < pathSegments.Length
                    && MatchSegments(pathSegments, patternIndex, pathIndex + 1, memo));
        }
        else
        {
            matched = pathIndex < pathSegments.Length
                && MatchSingleSegment(_segments[patternIndex], pathSegments[pathIndex])
                && MatchSegments(pathSegments, patternIndex + 1, pathIndex + 1, memo);
        }

        memo[patternIndex, pathIndex] = matched ? (sbyte)1 : (sbyte)-1;
        return matched;
    }

    /// <summary>
    /// 单段通配匹配。采用回溯式双指针，只回退到最近一个星号，避免为每个模式生成正则；
    /// Windows 上按大小写不敏感比较，与项目路径规则一致。
    /// </summary>
    private static bool MatchSingleSegment(string pattern, string value)
    {
        var patternIndex = 0;
        var valueIndex = 0;
        var starIndex = -1;
        var starValueIndex = -1;

        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == '?' || CharactersEqual(pattern[patternIndex], value[valueIndex])))
            {
                patternIndex++;
                valueIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                starValueIndex = valueIndex;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++starValueIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    private static bool CharactersEqual(char left, char right)
    {
        return left == right
            || (OperatingSystem.IsWindows()
                && char.ToUpperInvariant(left) == char.ToUpperInvariant(right));
    }
}
