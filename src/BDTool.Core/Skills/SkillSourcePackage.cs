using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BDTool.Contracts.Errors;
using BDTool.Core.Workspace;

namespace BDTool.Core.Skills;

/// <summary>
/// 已校验并读入内存的源 Skill 树。安装时使用同一份字节快照完成校验和与复制，
/// 避免检查后源文件变化造成“校验的是一个版本、安装的是另一个版本”。
/// </summary>
internal sealed class SkillSourcePackage
{
    private const string SkillFileName = "SKILL.md";

    private SkillSourcePackage(
        string name,
        string sourceRoot,
        string checksum,
        IReadOnlyList<SkillSourceFile> files)
    {
        Name = name;
        SourceRoot = sourceRoot;
        Checksum = checksum;
        Files = files;
    }

    public string Name { get; }

    public string SourceRoot { get; }

    public string Checksum { get; }

    public IReadOnlyList<SkillSourceFile> Files { get; }

    /// <summary>
    /// 读取一个必需 Skill。目录、所有子目录与文件都不得是 reparse point，
    /// 且根目录必须直接包含 SKILL.md；任何非法源都在目标写入前失败。
    /// </summary>
    public static SkillSourcePackage Load(string sourceSkillsRoot, string skillName)
    {
        var packageRoot = Path.GetFullPath(Path.Combine(sourceSkillsRoot, skillName));
        EnsureWithin(sourceSkillsRoot, packageRoot);

        if (!Directory.Exists(packageRoot))
        {
            throw new SkillInstallerException(
                ErrorCodes.NotFound,
                "源 Skill 目录不存在：" + skillName);
        }

        if (PathPolicy.IsReparsePoint(packageRoot))
        {
            throw new SkillInstallerException(
                ErrorCodes.InvalidArgument,
                "源 Skill 目录不能是 reparse point：" + skillName);
        }

        var files = new List<SkillSourceFile>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(packageRoot);

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();
            string[] entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(currentDirectory).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new SkillInstallerException(
                    ErrorCodes.ExecutionFailed,
                    "读取源 Skill 目录失败：" + skillName,
                    exception);
            }

            foreach (var entry in entries)
            {
                EnsureWithin(packageRoot, entry);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new SkillInstallerException(
                        ErrorCodes.ExecutionFailed,
                        "读取源 Skill 文件属性失败：" + skillName,
                        exception);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new SkillInstallerException(
                        ErrorCodes.InvalidArgument,
                        "源 Skill 树包含 reparse point，拒绝遍历：" + skillName);
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(entry);
                    continue;
                }

                if ((attributes & FileAttributes.Device) != 0 || !File.Exists(entry))
                {
                    throw new SkillInstallerException(
                        ErrorCodes.InvalidArgument,
                        "源 Skill 树包含不支持的文件类型：" + skillName);
                }

                var relativePath = PathPolicy.NormalizeRelativePath(
                    Path.GetRelativePath(packageRoot, entry));
                byte[] content;
                try
                {
                    content = File.ReadAllBytes(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new SkillInstallerException(
                        ErrorCodes.ExecutionFailed,
                        "读取源 Skill 文件失败：" + relativePath,
                        exception);
                }

                files.Add(new SkillSourceFile(relativePath, content));
            }
        }

        if (!files.Any(file => string.Equals(file.RelativePath, SkillFileName, StringComparison.Ordinal)))
        {
            throw new SkillInstallerException(
                ErrorCodes.NotFound,
                "源 Skill 缺少 SKILL.md：" + skillName);
        }

        files.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return new SkillSourcePackage(skillName, packageRoot, ComputeChecksum(files), files);
    }

    /// <summary>
    /// 路径与长度的显式编码消除了拼接歧义：不同文件布局不会因为简单字符串相连而得到同一摘要。
    /// </summary>
    private static string ComputeChecksum(IReadOnlyList<SkillSourceFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthBytes = stackalloc byte[4];

        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
            hash.AppendData(new byte[] { 0 });
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, file.Content.Length);
            hash.AppendData(lengthBytes);
            hash.AppendData(file.Content);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void EnsureWithin(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        if (!PathPolicy.IsWithinProjectRoot(normalizedRoot, normalizedPath))
        {
            throw new SkillInstallerException(
                ErrorCodes.InvalidArgument,
                "源 Skill 路径越出源技能根目录。");
        }
    }
}

/// <summary>源 Skill 中一个已读入内存的相对路径文件。</summary>
internal sealed class SkillSourceFile
{
    public SkillSourceFile(string relativePath, byte[] content)
    {
        RelativePath = relativePath;
        Content = content;
    }

    /// <summary>使用正斜杠的相对路径，可直接拆分为目标目录层级。</summary>
    public string RelativePath { get; }

    public byte[] Content { get; }
}
