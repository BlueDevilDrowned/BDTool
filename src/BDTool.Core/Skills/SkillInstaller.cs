using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Skills;
using BDTool.Core.IO;
using BDTool.Core.Workspace;

namespace BDTool.Core.Skills;

/// <summary>
/// 把发布包内的固定 Skill 安装到项目的 .agents/skills。安装器只接管带有
/// .bdtool-managed 标记的目录，并在任何复制前完成全部目标的冲突预检。
/// </summary>
public sealed class SkillInstaller
{
    /// <summary>当前唯一支持的安装作用域。</summary>
    public const string ProjectScope = "project";

    private const string SkillsDirectory = ".agents";
    private const string SkillsFolder = "skills";

    // 顺序同时决定结果列表的稳定顺序。新增必需 Skill 必须先更新产品合同与测试。
    private static readonly string[] s_requiredSkillNames =
    {
        "bdtool-overview",
        "bdtool-unity-work"
    };

    private readonly string _sourceSkillsRoot;

    /// <summary>
    /// 创建安装器。这里只规范化源根路径；源目录与必需 Skill 是否齐全在 Install
    /// 阶段检查，便于调用方先构造安装器再处理实际安装失败。
    /// </summary>
    public SkillInstaller(string sourceSkillsRoot)
    {
        if (sourceSkillsRoot == null)
        {
            throw new ArgumentNullException(nameof(sourceSkillsRoot));
        }

        if (string.IsNullOrWhiteSpace(sourceSkillsRoot))
        {
            throw new SkillInstallerException(
                ErrorCodes.InvalidArgument,
                "源 Skill 根目录不能为空。");
        }

        try
        {
            _sourceSkillsRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(sourceSkillsRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SkillInstallerException(
                ErrorCodes.InvalidArgument,
                "源 Skill 根目录不是合法路径。",
                exception);
        }
    }

    /// <summary>
    /// 把一个项目安装为 project 作用域。成功时 Skills 按固定顺序返回两个 Skill；
    /// 任一目标冲突时返回 Conflicts 且不创建、不覆盖任何 Skill 文件。
    /// </summary>
    public SkillInstallResult Install(string projectRoot, string scope = ProjectScope)
    {
        if (!string.Equals(scope, ProjectScope, StringComparison.Ordinal))
        {
            throw new SkillInstallerException(
                ErrorCodes.InvalidArgument,
                "仅支持 project Skill 安装作用域。");
        }

        try
        {
            return InstallCore(projectRoot);
        }
        catch (SkillInstallerException)
        {
            throw;
        }
        catch (PathPolicyException exception)
        {
            throw new SkillInstallerException(
                ErrorCodes.InvalidArgument,
                "Skill 安装路径不安全：" + exception.Message,
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SkillInstallerException(
                ErrorCodes.ExecutionFailed,
                "Skill 安装写入失败。",
                exception);
        }
    }

    private SkillInstallResult InstallCore(string projectRoot)
    {
        var normalizedProjectRoot = NormalizeExistingProject(projectRoot);
        var skillsRoot = Path.Combine(normalizedProjectRoot, SkillsDirectory, SkillsFolder);
        PathPolicy.EnsureNoReparsePoint(normalizedProjectRoot, skillsRoot);
        EnsureDirectoryPathIsUsable(normalizedProjectRoot, skillsRoot);

        if (PathPolicy.IsReparsePoint(_sourceSkillsRoot))
        {
            throw new SkillInstallerException(
                ErrorCodes.InvalidArgument,
                "源 Skill 根目录不能是 reparse point。");
        }

        // 先完整读取两个源树，再做两个目标的全量冲突预检。任何一步失败都不会触碰目标。
        var packages = s_requiredSkillNames
            .Select(name => SkillSourcePackage.Load(_sourceSkillsRoot, name))
            .ToArray();

        var conflicts = new List<SkillConflict>();
        var managedDestinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            var destinationPath = Path.Combine(skillsRoot, package.Name);
            var state = PreflightDestination(normalizedProjectRoot, destinationPath, package.Name);
            if (state.Conflict is not null)
            {
                conflicts.Add(state.Conflict);
            }
            else if (state.ManagedDirectoryExists)
            {
                managedDestinations.Add(package.Name);
            }
        }

        if (conflicts.Count > 0)
        {
            return CreateResult(normalizedProjectRoot, skillsRoot, skills: null, conflicts);
        }

        Directory.CreateDirectory(skillsRoot);
        PathPolicy.EnsureNoReparsePoint(normalizedProjectRoot, skillsRoot);

        var installedSkills = new List<InstalledSkill>(packages.Length);
        foreach (var package in packages)
        {
            var destinationPath = Path.Combine(skillsRoot, package.Name);
            CopyPackage(normalizedProjectRoot, package, destinationPath);
            installedSkills.Add(new InstalledSkill
            {
                Name = package.Name,
                DestinationPath = destinationPath,
                SourcePath = package.SourceRoot,
                Updated = managedDestinations.Contains(package.Name)
            });
        }

        return CreateResult(normalizedProjectRoot, skillsRoot, installedSkills, conflicts);
    }

    /// <summary>规范化并要求项目根真实存在；缺失项目不会被自动创建。</summary>
    private static string NormalizeExistingProject(string projectRoot)
    {
        string normalized;
        try
        {
            normalized = PathPolicy.NormalizeProjectRoot(projectRoot);
        }
        catch (PathPolicyException exception)
        {
            throw new SkillInstallerException(
                ErrorCodes.InvalidArgument,
                "项目路径不合法：" + exception.Message,
                exception);
        }

        if (!Directory.Exists(normalized))
        {
            throw new SkillInstallerException(
                ErrorCodes.ProjectNotFound,
                "项目目录不存在：" + normalized);
        }

        if (PathPolicy.IsReparsePoint(normalized))
        {
            throw new SkillInstallerException(
                ErrorCodes.InvalidArgument,
                "项目根目录不能是 reparse point。");
        }

        return normalized;
    }

    /// <summary>
    /// 检查已有 .agents 或 skills 是否被文件占据。真正创建目录发生在两个目标都通过冲突检查之后。
    /// </summary>
    private static void EnsureDirectoryPathIsUsable(string projectRoot, string skillsRoot)
    {
        var agentsRoot = Path.Combine(projectRoot, SkillsDirectory);
        foreach (var path in new[] { agentsRoot, skillsRoot })
        {
            if (File.Exists(path) && !Directory.Exists(path))
            {
                throw new SkillInstallerException(
                    ErrorCodes.PreconditionFailed,
                    "Skill 目标父路径被文件占用：" + path);
            }
        }
    }

    /// <summary>
    /// 仅检查一个目标目录是否可被接管。带合法标记的目录视为受管目录；
    /// 其余任何已存在形式都转为冲突，由调用方一次性返回全部问题。
    /// </summary>
    private static DestinationPreflight PreflightDestination(
        string projectRoot,
        string destinationPath,
        string skillName)
    {
        if (!Directory.Exists(destinationPath) && !File.Exists(destinationPath))
        {
            return DestinationPreflight.Fresh();
        }

        if (PathPolicy.IsReparsePoint(destinationPath))
        {
            return DestinationPreflight.CreateConflict(skillName, destinationPath, "目标目录是 reparse point，拒绝遍历。");
        }

        if (!Directory.Exists(destinationPath))
        {
            return DestinationPreflight.CreateConflict(skillName, destinationPath, "目标路径已存在但不是目录。");
        }

        var markerPath = Path.Combine(destinationPath, ManagedSkillMarker.FileName);
        if (PathPolicy.IsReparsePoint(markerPath))
        {
            return DestinationPreflight.CreateConflict(skillName, destinationPath, "受管标记是 reparse point，拒绝读取。");
        }

        if (Directory.Exists(markerPath))
        {
            return DestinationPreflight.CreateConflict(skillName, destinationPath, "受管标记路径不是文件。");
        }

        if (!File.Exists(markerPath))
        {
            return DestinationPreflight.CreateConflict(
                skillName,
                destinationPath,
                "目标目录已存在且缺少 .bdtool-managed。");
        }

        string markerContent;
        try
        {
            markerContent = File.ReadAllText(markerPath, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DestinationPreflight.CreateConflict(skillName, destinationPath, "受管标记不可读：" + exception.Message);
        }

        if (!ManagedSkillMarker.TryParse(markerContent, skillName, out _))
        {
            return DestinationPreflight.CreateConflict(skillName, destinationPath, "受管标记格式无效。");
        }

        PathPolicy.EnsureNoReparsePoint(projectRoot, destinationPath);
        var unsafeEntry = FindUnsafeManagedEntry(destinationPath);
        if (unsafeEntry is not null)
        {
            return DestinationPreflight.CreateConflict(skillName, destinationPath, unsafeEntry);
        }

        return DestinationPreflight.Managed();
    }

    /// <summary>
    /// 在复制前扫描受管目录的现有内容。嵌套目录链接同样属于遍历风险，
    /// 必须作为冲突在其他 Skill 开始复制之前返回，避免出现只更新一半的结果。
    /// </summary>
    private static string? FindUnsafeManagedEntry(string destinationPath)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(destinationPath);

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
                return "受管目录不可读：" + exception.Message;
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return "受管目录项不可读：" + exception.Message;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var relativePath = Path.GetRelativePath(destinationPath, entry);
                    return "受管目录包含 reparse point：" + relativePath;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(entry);
                }
                else if ((attributes & FileAttributes.Device) != 0 || !File.Exists(entry))
                {
                    return "受管目录包含不支持的文件类型。";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 原子刷新一个已通过预检的 Skill 目录。每个文件保留源树的相对布局，
    /// 目标文件与标记均通过 AtomicFile 替换，降低中途失败留下半写文件的风险。
    /// </summary>
    private static void CopyPackage(
        string projectRoot,
        SkillSourcePackage package,
        string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        PathPolicy.EnsureNoReparsePoint(projectRoot, destinationPath);

        foreach (var file in package.Files)
        {
            var nativeRelativePath = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var targetPath = Path.GetFullPath(Path.Combine(destinationPath, nativeRelativePath));
            EnsureWithin(destinationPath, targetPath);

            var parentDirectory = Path.GetDirectoryName(targetPath)
                ?? throw new SkillInstallerException(
                    ErrorCodes.InvalidArgument,
                    "Skill 目标文件缺少父目录：" + file.RelativePath);
            Directory.CreateDirectory(parentDirectory);

            // 逐段复查，防止并发替换或目标树中预先存在的目录链接把写入带到项目外。
            PathPolicy.EnsureNoReparsePoint(projectRoot, parentDirectory);
            PathPolicy.EnsureNoReparsePoint(projectRoot, targetPath);
            WriteAtomically(targetPath, file.Content);
        }

        var markerPath = Path.Combine(destinationPath, ManagedSkillMarker.FileName);
        PathPolicy.EnsureNoReparsePoint(projectRoot, markerPath);
        WriteAtomically(
            markerPath,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetBytes(ManagedSkillMarker.Create(package.Name, package.Checksum)));
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        AtomicFile.WriteAsync(path, content, overwrite: true, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private static void EnsureWithin(string root, string path)
    {
        if (!PathPolicy.IsWithinProjectRoot(root, path))
        {
            throw new SkillInstallerException(
                ErrorCodes.InvalidArgument,
                "Skill 目标路径越出受管目录。");
        }
    }

    private static SkillInstallResult CreateResult(
        string projectRoot,
        string skillsRoot,
        List<InstalledSkill>? skills,
        List<SkillConflict> conflicts)
    {
        return new SkillInstallResult
        {
            Scope = ProjectScope,
            ProjectPath = projectRoot,
            SkillsRoot = skillsRoot,
            Skills = skills ?? new List<InstalledSkill>(),
            Conflicts = conflicts
        };
    }

    /// <summary>目标预检结果。冲突与受管目录状态互斥，避免后续复制阶段再次猜测。</summary>
    private sealed class DestinationPreflight
    {
        private DestinationPreflight(SkillConflict? conflict, bool managedDirectoryExists)
        {
            Conflict = conflict;
            ManagedDirectoryExists = managedDirectoryExists;
        }

        public SkillConflict? Conflict { get; }

        public bool ManagedDirectoryExists { get; }

        public static DestinationPreflight Fresh()
        {
            return new DestinationPreflight(conflict: null, managedDirectoryExists: false);
        }

        public static DestinationPreflight Managed()
        {
            return new DestinationPreflight(conflict: null, managedDirectoryExists: true);
        }

        public static DestinationPreflight CreateConflict(string name, string destinationPath, string reason)
        {
            return new DestinationPreflight(
                new SkillConflict
                {
                    Name = name,
                    DestinationPath = destinationPath,
                    Reason = reason
                },
                managedDirectoryExists: false);
        }
    }
}
