using System;
using System.IO;

namespace BDTool.Core.Projects;

/// <summary>
/// 项目内 BDTool 目录布局。所有路径都由项目根推导，调用方不得自行拼接，
/// 以免出现第二套 Activity 或 artifact 目录。
/// </summary>
public static class ProjectLayout
{
    /// <summary>返回项目的规范化绝对路径。</summary>
    public static string ResolveProjectRoot(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("项目路径不能为空。", nameof(projectPath));
        }

        return Path.GetFullPath(projectPath);
    }

    /// <summary>返回 &lt;project&gt;/.bdtool。</summary>
    public static string GetStateRoot(string projectPath)
    {
        var root = ResolveProjectRoot(projectPath);
        return Path.Combine(root, ActivityPolicyDirectoryName);
    }

    /// <summary>返回 &lt;project&gt;/.bdtool/state/activity。</summary>
    public static string GetActivityRoot(string projectPath)
    {
        return Path.Combine(
            GetStateRoot(projectPath),
            "state",
            "activity");
    }

    /// <summary>返回 &lt;project&gt;/.bdtool/artifacts。</summary>
    public static string GetArtifactsRoot(string projectPath)
    {
        return Path.Combine(GetStateRoot(projectPath), "artifacts");
    }

    private const string ActivityPolicyDirectoryName = ".bdtool";
}
