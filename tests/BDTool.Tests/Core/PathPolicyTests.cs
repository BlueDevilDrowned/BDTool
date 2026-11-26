using System;
using System.IO;
using BDTool.Core.Workspace;

namespace BDTool.Tests.Core;

/// <summary>Local Workspace 的路径规范化和越界防护。</summary>
public sealed class PathPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "bdtool-path-tests",
        Guid.NewGuid().ToString("n"));

    public PathPolicyTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void NormalizeProjectRoot_ReturnsAbsoluteRootWithoutTrailingSeparator()
    {
        var normalized = PathPolicy.NormalizeProjectRoot(_root + Path.DirectorySeparatorChar);

        Assert.True(Path.IsPathFullyQualified(normalized));
        Assert.Equal(_root, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/absolute")]
    [InlineData("\\server\\share\\file.cs")]
    [InlineData("C:\\absolute.cs")]
    [InlineData("C:relative")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("Assets/../Secrets.cs")]
    [InlineData("Assets/./Main.cs")]
    [InlineData("Assets//Main.cs")]
    [InlineData("Assets/Main.cs:stream")]
    [InlineData("Assets/CON")]
    [InlineData("Assets/con.txt")]
    [InlineData("Assets/CON .txt")]
    [InlineData("Assets/COM¹.txt")]
    [InlineData("Assets/trailing.")]
    [InlineData("Assets/trailing ")]
    [InlineData("Assets/bad*name.cs")]
    [InlineData("Assets/bad?name.cs")]
    [InlineData("Assets/\0name.cs")]
    public void NormalizeRelativePath_RejectsUnsafeForms(string relativePath)
    {
        var exception = Assert.Throws<PathPolicyException>(() =>
            PathPolicy.NormalizeRelativePath(relativePath));

        Assert.Equal(PathPolicyViolation.InvalidPath, exception.Violation);
    }

    [Theory]
    [InlineData("Assets/Main.cs", "Assets/Main.cs")]
    [InlineData("Assets\\Scripts\\Main.cs", "Assets/Scripts/Main.cs")]
    [InlineData(".gitignore", ".gitignore")]
    [InlineData("Library/ScriptAssemblies/BDTool.dll", "Library/ScriptAssemblies/BDTool.dll")]
    public void NormalizeRelativePath_UsesCanonicalForwardSlashes(string input, string expected)
    {
        Assert.Equal(expected, PathPolicy.NormalizeRelativePath(input));
    }

    [Fact]
    public void ResolveRelativePath_ReturnsPathInsideProjectRoot()
    {
        var resolved = PathPolicy.ResolveRelativePath(_root, "Assets/Scripts/Main.cs");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "Assets", "Scripts", "Main.cs")),
            resolved);
    }

    [Fact]
    public void IsWithinProjectRoot_RejectsSiblingPrefixCollision()
    {
        Assert.True(PathPolicy.IsWithinProjectRoot(_root, Path.Combine(_root, "Assets", "Main.cs")));
        Assert.False(PathPolicy.IsWithinProjectRoot(_root, Path.Combine(_root + "2", "Main.cs")));
        Assert.False(PathPolicy.IsWithinProjectRoot(_root, _root));
    }

    [Fact]
    public void IsWithinProjectRoot_UsesCaseInsensitiveComparisonOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(PathPolicy.IsWithinProjectRoot(
            _root,
            Path.Combine(_root.ToUpperInvariant(), "Assets", "Main.cs")));
    }

    [Fact]
    public void EnsureNoReparsePoint_AllowsMissingDescendantAndNormalFile()
    {
        var missing = PathPolicy.ResolveRelativePath(_root, "Assets/New/Main.cs");
        PathPolicy.EnsureNoReparsePoint(_root, missing);

        var file = PathPolicy.ResolveRelativePath(_root, "Main.cs");
        File.WriteAllText(file, "content");

        Assert.False(PathPolicy.IsReparsePoint(file));
        PathPolicy.EnsureNoReparsePoint(_root, file);
    }

    [Fact]
    public void EnsureNoReparsePoint_RejectsDirectorySymlinkWhenPlatformAllowsIt()
    {
        var target = Path.Combine(_root, "target");
        var link = Path.Combine(_root, "link");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 没有创建符号链接权限的环境无法验证该分支；不伪造通过，直接结束。
            return;
        }

        try
        {
            var exception = Assert.Throws<PathPolicyException>(() =>
                PathPolicy.EnsureNoReparsePoint(_root, Path.Combine(link, "Main.cs")));
            Assert.Equal(PathPolicyViolation.ReparsePoint, exception.Violation);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
