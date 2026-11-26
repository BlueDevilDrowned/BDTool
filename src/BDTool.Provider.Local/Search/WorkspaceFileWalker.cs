using System;
using System.Collections.Generic;
using System.IO;
using BDTool.Core.Workspace;

namespace BDTool.Provider.Local.Search;

internal sealed class WorkspaceWalkedFile
{
    public WorkspaceWalkedFile(string absolutePath, string relativePath)
    {
        AbsolutePath = absolutePath;
        RelativePath = relativePath;
    }

    public string AbsolutePath { get; }

    public string RelativePath { get; }
}

internal sealed class WorkspaceWalkResult
{
    public WorkspaceWalkResult(List<WorkspaceWalkedFile> files, int skippedReparse)
    {
        Files = files;
        SkippedReparse = skippedReparse;
    }

    public List<WorkspaceWalkedFile> Files { get; }

    public int SkippedReparse { get; }
}

/// <summary>
/// 安全的 Workspace 文件遍历。固定排除运行期/生成目录；遇到 reparse 文件或目录时不再跟随，
/// 并以 SkippedReparse 计数。遍历结果再做一次全局路径排序，保证搜索结果不受文件系统枚举顺序影响。
/// </summary>
internal static class WorkspaceFileWalker
{
    private static readonly HashSet<string> s_excludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".bdtool",
        "bin",
        "obj",
        "Library",
        "Temp",
        "Logs",
        "UserSettings",
        "node_modules",
        ".vs"
    };

    private static readonly EnumerationOptions s_enumerationOptions = new()
    {
        // 默认枚举会跳过 Hidden/System；Workspace 搜索只遵循契约中的固定排除表。
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    private static readonly StringComparer s_pathComparer = StringComparer.OrdinalIgnoreCase;

    public static WorkspaceWalkResult Walk(
        string projectRoot,
        WorkspaceGlob glob,
        CancellationToken cancellationToken)
    {
        var files = new List<WorkspaceWalkedFile>();
        var skippedReparse = 0;
        WalkDirectory(
            projectRoot,
            currentDirectory: projectRoot,
            relativePrefix: string.Empty,
            glob,
            files,
            ref skippedReparse,
            cancellationToken);

        files.Sort(CompareFiles);
        return new WorkspaceWalkResult(files, skippedReparse);
    }

    private static void WalkDirectory(
        string projectRoot,
        string currentDirectory,
        string relativePrefix,
        WorkspaceGlob glob,
        List<WorkspaceWalkedFile> files,
        ref int skippedReparse,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var filePaths = new List<string>(Directory.EnumerateFiles(
            currentDirectory,
            "*",
            s_enumerationOptions));
        filePaths.Sort((left, right) => s_pathComparer.Compare(
            Path.GetFileName(left),
            Path.GetFileName(right)));

        foreach (var filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = CombineRelativePath(relativePrefix, Path.GetFileName(filePath));
            if (!glob.IsMatch(relativePath))
            {
                continue;
            }

            if (PathPolicy.IsReparsePoint(filePath))
            {
                skippedReparse++;
                continue;
            }

            files.Add(new WorkspaceWalkedFile(filePath, relativePath));
        }

        var directoryPaths = new List<string>(Directory.EnumerateDirectories(
            currentDirectory,
            "*",
            s_enumerationOptions));
        directoryPaths.Sort((left, right) => s_pathComparer.Compare(
            Path.GetFileName(left),
            Path.GetFileName(right)));

        foreach (var directoryPath in directoryPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryName = Path.GetFileName(directoryPath);
            if (s_excludedDirectories.Contains(directoryName))
            {
                continue;
            }

            if (PathPolicy.IsReparsePoint(directoryPath))
            {
                skippedReparse++;
                continue;
            }

            WalkDirectory(
                projectRoot,
                directoryPath,
                CombineRelativePath(relativePrefix, directoryName),
                glob,
                files,
                ref skippedReparse,
                cancellationToken);
        }
    }

    private static string CombineRelativePath(string prefix, string name)
    {
        return prefix.Length == 0 ? name : prefix + "/" + name;
    }

    private static int CompareFiles(WorkspaceWalkedFile left, WorkspaceWalkedFile right)
    {
        var comparison = s_pathComparer.Compare(left.RelativePath, right.RelativePath);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath);
    }
}
