using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Workspace;
using BDTool.Core.Workspace;

namespace BDTool.Tests.Workspace;

/// <summary>workspace.search 的字面量、glob、排除、排序和跳过计数语义。</summary>
public sealed class LocalWorkspaceSearchTests : IDisposable
{
    private readonly WorkspaceTestProject _project = new();

    [Fact]
    public async Task SearchAsync_FindsLiteralMatchesAcrossTreeWithDefaultGlob()
    {
        _project.WriteText("README.md", "root needle\n");
        _project.WriteText("Assets/Scripts/Main.cs", "line one\nneedle two\n");
        _project.WriteText("Assets/Docs/Guide.md", "needle three\r\n");

        var result = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("needle"),
            CancellationToken.None);

        Assert.False(result.Truncated);
        Assert.Equal(
            new[] { "Assets/Docs/Guide.md", "Assets/Scripts/Main.cs", "README.md" },
            result.Matches.Select(match => match.RelativePath).ToArray());
        Assert.Equal(new[] { 1, 2, 1 }, result.Matches.Select(match => match.LineNumber).ToArray());
        Assert.Equal(0, result.SkippedBinary);
        Assert.Equal(0, result.SkippedTooLarge);
        Assert.Equal(0, result.SkippedSymlink);
    }

    [Fact]
    public async Task SearchAsync_IsCaseSensitiveByDefault()
    {
        _project.WriteText("lower.txt", "needle\n");
        _project.WriteText("upper.txt", "Needle\n");

        var result = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("Needle"),
            CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Equal("upper.txt", match.RelativePath);
    }

    [Fact]
    public async Task SearchAsync_TreatsQueryAsLiteralText()
    {
        _project.WriteText("meta.txt", "axb\na.b\n");

        var result = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("a.b"),
            CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Equal(2, match.LineNumber);
    }

    [Fact]
    public async Task SearchAsync_ReturnsLineTextWithoutLineTerminators()
    {
        _project.WriteText("lines.txt", "first\r\nneedle here\r\nlast\n");

        var result = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("needle"),
            CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Equal("needle here", match.Line);
    }

    [Fact]
    public async Task SearchAsync_SingleStarDoesNotCrossDirectorySeparators()
    {
        _project.WriteText("Root.cs", "needle\n");
        _project.WriteText("Nested/Child.cs", "needle\n");
        _project.WriteText("a.cs", "needle\n");
        _project.WriteText("ab.cs", "needle\n");

        var starResult = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("needle", "*.cs"),
            CancellationToken.None);
        Assert.Equal(
            new[] { "a.cs", "ab.cs", "Root.cs" },
            starResult.Matches.Select(match => match.RelativePath).ToArray());

        var questionResult = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("needle", "?.cs"),
            CancellationToken.None);
        var questionMatch = Assert.Single(questionResult.Matches);
        Assert.Equal("a.cs", questionMatch.RelativePath);
    }

    [Fact]
    public async Task SearchAsync_DoubleStarCrossesDirectories()
    {
        _project.WriteText("Assets/One.cs", "needle\n");
        _project.WriteText("Assets/A/B/Two.cs", "needle\n");
        _project.WriteText("Other/Three.cs", "needle\n");

        var result = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("needle", "Assets/**/*.cs"),
            CancellationToken.None);

        Assert.Equal(
            new[] { "Assets/A/B/Two.cs", "Assets/One.cs" },
            result.Matches.Select(match => match.RelativePath).ToArray());
    }

    [Fact]
    public async Task SearchAsync_GlobUsesForwardSlashPaths()
    {
        _project.WriteText("Assets/Scripts/Main.cs", "needle\n");

        var result = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("needle", "**/*.cs"),
            CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Equal("Assets/Scripts/Main.cs", match.RelativePath);
        Assert.DoesNotContain('\\', match.RelativePath);
    }

    [Fact]
    public async Task SearchAsync_ExcludesGeneratedAndRuntimeDirectories()
    {
        var excluded = new[]
        {
            ".git/config",
            ".bdtool/state.json",
            "bin/generated.cs",
            "BIN/upper.cs",
            "obj/generated.cs",
            "Library/PackageCache/x.cs",
            "Temp/cache.cs",
            "Logs/run.cs",
            "UserSettings/Layout.cs",
            "node_modules/pkg/index.js",
            ".vs/workspace.cs"
        };

        foreach (var path in excluded)
        {
            _project.WriteText(path, "needle\n");
        }

        _project.WriteText("src/keep.cs", "needle\n");

        var result = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("needle"),
            CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Equal("src/keep.cs", match.RelativePath);
    }

    [Fact]
    public async Task SearchAsync_TruncatesAfterStablePathAndLineOrdering()
    {
        _project.WriteText("d.txt", "needle\n");
        _project.WriteText("c.txt", "needle\n");
        _project.WriteText("b.txt", "needle\n");
        _project.WriteText("a.txt", "needle\n");

        var result = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("needle", maxResults: 2),
            CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Equal(
            new[] { "a.txt", "b.txt" },
            result.Matches.Select(match => match.RelativePath).ToArray());
    }

    [Fact]
    public async Task SearchAsync_ClampsRequestMaxResultsToProviderLimit()
    {
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt", "d.txt" })
        {
            _project.WriteText(name, "needle\n");
        }

        var kernel = _project.CreateKernel(options => options.MaxSearchResults = 2);
        var result = await kernel.SearchAsync(
            _project.SearchRequest("needle", maxResults: 10),
            CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Equal(2, result.Matches.Count);
    }

    [Fact]
    public async Task SearchAsync_CountsBinaryAndNulFiles()
    {
        _project.WriteBytes("invalid.bin", new byte[] { 0x41, 0xC3, 0x28 });
        _project.WriteBytes("nul.txt", Encoding.UTF8.GetBytes("needle\0hidden"));
        _project.WriteText("text.txt", "needle\n");

        var result = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("needle"),
            CancellationToken.None);

        Assert.Equal(2, result.SkippedBinary);
        var match = Assert.Single(result.Matches);
        Assert.Equal("text.txt", match.RelativePath);
    }

    [Fact]
    public async Task SearchAsync_CountsFilesAboveSearchLimit()
    {
        _project.WriteText("large.txt", "0123456789needle");
        _project.WriteText("small.txt", "needle\n");
        var kernel = _project.CreateKernel(options => options.MaxSearchFileBytes = 8);

        var result = await kernel.SearchAsync(
            _project.SearchRequest("needle"),
            CancellationToken.None);

        Assert.Equal(1, result.SkippedTooLarge);
        var match = Assert.Single(result.Matches);
        Assert.Equal("small.txt", match.RelativePath);
    }

    [ReparsePointFact]
    public async Task SearchAsync_CountsReparsePointsWithoutFollowingThem()
    {
        _project.WriteText("real.txt", "needle\n");
        var target = Path.Combine(Path.GetTempPath(), "bdtool-search-target-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "outside.txt"), "needle\n");
        var link = _project.Resolve("Linked");

        // 发现阶段的 ReparsePointFact 已确认该环境能创建目录链接；此处失败属于环境异常，必须报错。
        if (!WorkspaceReparsePoints.TryCreateDirectoryLink(link, target))
        {
            Directory.Delete(target, recursive: true);
            Assert.Fail("发现阶段探测显示目录链接可用，但运行阶段创建失败。");
        }

        try
        {
            var result = await _project.CreateKernel().SearchAsync(
                _project.SearchRequest("needle"),
                CancellationToken.None);

            Assert.True(result.SkippedSymlink >= 1);
            Assert.Equal(
                new[] { "real.txt" },
                result.Matches.Select(match => match.RelativePath).ToArray());
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(target, recursive: true);
        }
    }

    [Theory]
    [InlineData("../*.cs")]
    [InlineData("Assets/../*.cs")]
    [InlineData("/absolute/*.cs")]
    [InlineData("C:/project/*.cs")]
    [InlineData("a\\b.cs")]
    [InlineData("Assets//Main.cs")]
    [InlineData("Assets/./Main.cs")]
    [InlineData("Assets/Main.cs/")]
    [InlineData("Assets/*.")]
    [InlineData("Assets/\u0001.cs")]
    public async Task SearchAsync_RejectsInvalidGlob(string glob)
    {
        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().SearchAsync(
                _project.SearchRequest("needle", glob),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
    }

    [Fact]
    public async Task SearchAsync_RejectsNonPositiveMaxResults()
    {
        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().SearchAsync(
                _project.SearchRequest("needle", maxResults: 0),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
    }

    [WindowsFact]
    public async Task SearchAsync_GlobIsCaseInsensitiveOnWindows()
    {
        _project.WriteText("Assets/Main.CS", "needle\n");

        var result = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("needle", "assets/**/*.cs"),
            CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Equal("Assets/Main.CS", match.RelativePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public async Task SearchAsync_RejectsEmptyOrWhitespaceQuery(string query)
    {
        _project.WriteText("a.txt", "needle\n");

        var exception = await Assert.ThrowsAsync<WorkspaceException>(() =>
            _project.CreateKernel().SearchAsync(
                _project.SearchRequest(query),
                CancellationToken.None));

        // 空串在 string.Contains 下对每一行都成立，必须在遍历前拒绝，而不是返回无界匹配。
        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyResultWhenNothingMatches()
    {
        _project.WriteText("a.txt", "alpha\n");
        _project.WriteText("b.txt", "beta\n");

        var result = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("needle"),
            CancellationToken.None);

        Assert.Empty(result.Matches);
        Assert.False(result.Truncated);
        Assert.Equal(0, result.SkippedBinary);
        Assert.Equal(0, result.SkippedTooLarge);
        Assert.Equal(0, result.SkippedSymlink);
    }

    [Fact]
    public async Task SearchAsync_ReturnsExactlyMaxResultsWithoutTruncation()
    {
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
        {
            _project.WriteText(name, "needle\n");
        }

        var result = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("needle", maxResults: 3),
            CancellationToken.None);

        Assert.Equal(3, result.Matches.Count);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task SearchAsync_TruncatesAtMaxResultsPlusOneAndKeepsStablePrefix()
    {
        // 写入顺序故意与路径顺序相反，验证“前 N 条”来自全局路径排序而非枚举顺序。
        foreach (var name in new[] { "d.txt", "c.txt", "b.txt", "a.txt" })
        {
            _project.WriteText(name, "needle\n");
        }

        var result = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("needle", maxResults: 3),
            CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Equal(
            new[] { "a.txt", "b.txt", "c.txt" },
            result.Matches.Select(match => match.RelativePath).ToArray());
    }

    [Fact]
    public async Task SearchAsync_KeepsSkipCountsAfterMatchLimitIsReached()
    {
        // 匹配达到 maxResults+1 后仍要读完剩余候选文件，Skipped* 才不会被截断影响。
        _project.WriteText("a.txt", "needle\n");
        _project.WriteText("b.txt", "needle\n");
        _project.WriteBytes("z.bin", new byte[] { 0x41, 0xC3, 0x28 });

        var result = await _project.CreateKernel().SearchAsync(
            _project.SearchRequest("needle", maxResults: 1),
            CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Equal("a.txt", Assert.Single(result.Matches).RelativePath);
        Assert.Equal(1, result.SkippedBinary);
    }

    public void Dispose()
    {
        _project.Dispose();
    }
}
