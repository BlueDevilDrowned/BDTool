using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BDTool.Contracts.Errors;
using BDTool.Core.Skills;

namespace BDTool.Tests.Core;

/// <summary>
/// SkillInstaller 的安装、受管标记、冲突预检与路径防护边界。
/// 所有源与目标都位于独立临时目录，测试不会读取或写入仓库 skills。
/// </summary>
public sealed class SkillInstallerTests : IDisposable
{
    private const string Overview = "bdtool-overview";
    private const string UnityWork = "bdtool-unity-work";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "bdtool-skill-installer-tests",
        Guid.NewGuid().ToString("n"));

    private readonly string _sourceRoot;
    private readonly string _projectRoot;

    public SkillInstallerTests()
    {
        _sourceRoot = Path.Combine(_root, "source-skills");
        _projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(_sourceRoot);
        Directory.CreateDirectory(_projectRoot);

        CreateSourceSkill(Overview, "overview-v1", "overview reference");
        CreateSourceSkill(UnityWork, "unity-v1", "unity reference");
    }

    [Fact]
    public void Install_FreshProject_CreatesBothSkillsWithReferencesAndMarkers()
    {
        var result = new SkillInstaller(_sourceRoot).Install(_projectRoot);

        Assert.Equal(SkillInstaller.ProjectScope, result.Scope);
        Assert.Equal(Path.GetFullPath(_projectRoot), result.ProjectPath);
        Assert.Equal(Path.Combine(_projectRoot, ".agents", "skills"), result.SkillsRoot);
        Assert.Empty(result.Conflicts);
        Assert.Equal(new[] { Overview, UnityWork }, result.Skills.Select(skill => skill.Name));
        Assert.All(result.Skills, skill => Assert.False(skill.Updated));

        foreach (var skill in result.Skills)
        {
            Assert.Equal(Destination(skill.Name), skill.DestinationPath);
            Assert.Equal(Path.Combine(_sourceRoot, skill.Name), skill.SourcePath);
            Assert.True(File.Exists(Path.Combine(skill.DestinationPath, "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(skill.DestinationPath, "references", "guide.md")));
            Assert.True(File.Exists(Marker(skill.Name)));

            var marker = File.ReadAllText(Marker(skill.Name));
            Assert.StartsWith("bdtool-managed-skill", marker, StringComparison.Ordinal);
            Assert.Contains("version=1", marker, StringComparison.Ordinal);
            Assert.Contains("name=" + skill.Name, marker, StringComparison.Ordinal);
            Assert.Contains("checksum=", marker, StringComparison.Ordinal);
        }

        Assert.Equal(
            "overview reference",
            File.ReadAllText(Path.Combine(Destination(Overview), "references", "guide.md")));
        Assert.Empty(Directory.EnumerateFiles(result.SkillsRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Install_SecondRun_RefreshesManagedSkillsAndReportsUpdated()
    {
        var installer = new SkillInstaller(_sourceRoot);
        var first = installer.Install(_projectRoot);
        Assert.All(first.Skills, skill => Assert.False(skill.Updated));

        File.WriteAllText(Path.Combine(_sourceRoot, Overview, "SKILL.md"), "overview-v2");
        var second = installer.Install(_projectRoot);

        Assert.Empty(second.Conflicts);
        Assert.Equal(2, second.Skills.Count);
        Assert.All(second.Skills, skill => Assert.True(skill.Updated));
        Assert.Equal("overview-v2", File.ReadAllText(Path.Combine(Destination(Overview), "SKILL.md")));
        Assert.False(Directory.Exists(Destination("bdtool-overview-copy")));
    }

    [Fact]
    public void Install_UnmanagedDestinationConflict_CopiesNeitherSkill()
    {
        var blockedDestination = Destination(Overview);
        Directory.CreateDirectory(blockedDestination);
        var sentinel = Path.Combine(blockedDestination, "user-file.txt");
        File.WriteAllText(sentinel, "keep me");

        var result = new SkillInstaller(_sourceRoot).Install(_projectRoot);

        Assert.Empty(result.Skills);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(Overview, conflict.Name);
        Assert.Equal(blockedDestination, conflict.DestinationPath);
        Assert.Contains(".bdtool-managed", conflict.Reason, StringComparison.Ordinal);

        Assert.Equal("keep me", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Marker(Overview)));
        Assert.False(Directory.Exists(Destination(UnityWork)));
        Assert.False(File.Exists(Path.Combine(Destination(UnityWork), "SKILL.md")));
    }

    [Fact]
    public void Install_InvalidManagedMarker_IsConflictWithoutOverwrite()
    {
        var destination = Destination(Overview);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Marker(Overview), "not-a-managed-marker");
        var sentinel = Path.Combine(destination, "keep.txt");
        File.WriteAllText(sentinel, "original");

        var result = new SkillInstaller(_sourceRoot).Install(_projectRoot);

        Assert.Empty(result.Skills);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(Overview, conflict.Name);
        Assert.Contains("标记", conflict.Reason, StringComparison.Ordinal);
        Assert.Equal("original", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(destination, "SKILL.md")));
        Assert.False(Directory.Exists(Destination(UnityWork)));
    }

    [Fact]
    public void Install_InvalidScopeAndRoots_ReturnStableErrorCodes()
    {
        var installer = new SkillInstaller(_sourceRoot);

        var scopeException = Assert.Throws<SkillInstallerException>(
            () => installer.Install(_projectRoot, "user"));
        Assert.Equal(ErrorCodes.InvalidArgument, scopeException.Code);
        Assert.False(Directory.Exists(Path.Combine(_projectRoot, ".agents")));

        var missingProject = Path.Combine(_root, "missing-project");
        var projectException = Assert.Throws<SkillInstallerException>(
            () => installer.Install(missingProject));
        Assert.Equal(ErrorCodes.ProjectNotFound, projectException.Code);
        Assert.False(Directory.Exists(missingProject));

        var missingSourceInstaller = new SkillInstaller(Path.Combine(_root, "missing-skills"));
        var sourceException = Assert.Throws<SkillInstallerException>(
            () => missingSourceInstaller.Install(_projectRoot));
        Assert.Equal(ErrorCodes.NotFound, sourceException.Code);
        Assert.False(Directory.Exists(Path.Combine(_projectRoot, ".agents")));

        var emptyRootException = Assert.Throws<SkillInstallerException>(
            () => new SkillInstaller("  "));
        Assert.Equal(ErrorCodes.InvalidArgument, emptyRootException.Code);
    }

    [Fact]
    public void Install_SourceSkillMissingSkillMd_FailsBeforeCreatingTargets()
    {
        var missingSkillFile = Path.Combine(_sourceRoot, UnityWork, "SKILL.md");
        File.Delete(missingSkillFile);

        var exception = Assert.Throws<SkillInstallerException>(
            () => new SkillInstaller(_sourceRoot).Install(_projectRoot));

        Assert.Equal(ErrorCodes.NotFound, exception.Code);
        Assert.False(Directory.Exists(Path.Combine(_projectRoot, ".agents")));
    }

    [Fact]
    public void Install_DestinationJunctionOutsideProject_IsRejectedAsConflict()
    {
        var outside = Path.Combine(_root, "outside");
        var skillsRoot = Path.Combine(_projectRoot, ".agents", "skills");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(skillsRoot);

        var linkedDestination = Destination(Overview);
        if (!TryCreateDirectoryJunction(linkedDestination, outside))
        {
            // Windows 未开放 junction 创建能力时无法真实触发该分支；不伪造通过。
            return;
        }

        try
        {
            var result = new SkillInstaller(_sourceRoot).Install(_projectRoot);

            Assert.Empty(result.Skills);
            var conflict = Assert.Single(result.Conflicts);
            Assert.Equal(Overview, conflict.Name);
            Assert.Contains("reparse point", conflict.Reason, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(outside, "SKILL.md")));
            Assert.False(Directory.Exists(Destination(UnityWork)));
        }
        finally
        {
            try
            {
                if (Directory.Exists(linkedDestination))
                {
                    Directory.Delete(linkedDestination);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void Install_NestedJunctionInsideManagedSkill_BlocksUpdateBeforeAnyCopy()
    {
        var installer = new SkillInstaller(_sourceRoot);
        installer.Install(_projectRoot);

        var outside = Path.Combine(_root, "outside-nested");
        var references = Path.Combine(Destination(Overview), "references");
        Directory.CreateDirectory(outside);
        Directory.Delete(references, recursive: true);

        if (!TryCreateDirectoryJunction(references, outside))
        {
            return;
        }

        try
        {
            File.WriteAllText(Path.Combine(_sourceRoot, Overview, "SKILL.md"), "must-not-be-copied");

            var result = installer.Install(_projectRoot);

            Assert.Empty(result.Skills);
            var conflict = Assert.Single(result.Conflicts);
            Assert.Equal(Overview, conflict.Name);
            Assert.Contains("reparse point", conflict.Reason, StringComparison.Ordinal);
            Assert.Equal(
                "overview-v1",
                File.ReadAllText(Path.Combine(Destination(Overview), "SKILL.md")));
            Assert.False(File.Exists(Path.Combine(outside, "guide.md")));
        }
        finally
        {
            try
            {
                if (Directory.Exists(references))
                {
                    Directory.Delete(references);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void CreateSourceSkill(string name, string skillText, string referenceText)
    {
        var root = Path.Combine(_sourceRoot, name);
        Directory.CreateDirectory(Path.Combine(root, "references"));
        File.WriteAllText(Path.Combine(root, "SKILL.md"), skillText);
        File.WriteAllText(Path.Combine(root, "references", "guide.md"), referenceText);
    }

    private string Destination(string skillName)
    {
        return Path.Combine(_projectRoot, ".agents", "skills", skillName);
    }

    private string Marker(string skillName)
    {
        return Path.Combine(Destination(skillName), ".bdtool-managed");
    }

    private static bool TryCreateDirectoryJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.HasExited
                && process.ExitCode == 0
                && Directory.Exists(linkPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
