using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Cli;
using BDTool.Tests.Support;

namespace BDTool.Tests.Cli;

/// <summary>
/// skills install 的 CLI 契约：真实读取输出目录下的发布 Skill，安装到独立临时项目。
/// 用例覆盖默认 JSON、人类输出、冲突不写入、作用域校验、二次安装与输出资源复制。
/// 安装不是 catalog operation，因此所有成功路径都必须保持 Activity 目录不存在。
/// </summary>
public sealed class SkillCliTests
{
    private const string Overview = "bdtool-overview";
    private const string UnityWork = "bdtool-unity-work";

    [Fact]
    public async Task Install_FreshProject_InstallsBothSkillsWithReferencesAndMarkers()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, stderr) = await RunAsync(
            "skills", "install", "--project", project.ProjectPath);

        Assert.True(exitCode == 0, $"exit={exitCode}\nstdout={stdout}\nstderr={stderr}");
        Assert.Equal(string.Empty, stderr);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("completed", root.GetProperty("status").GetString());
        var data = root.GetProperty("data");
        Assert.Equal("project", data.GetProperty("scope").GetString());
        Assert.Equal(Path.GetFullPath(project.ProjectPath), data.GetProperty("projectPath").GetString());
        Assert.Equal(
            Path.Combine(Path.GetFullPath(project.ProjectPath), ".agents", "skills"),
            data.GetProperty("skillsRoot").GetString());
        Assert.Empty(data.GetProperty("conflicts").EnumerateArray());

        var skills = data.GetProperty("skills").EnumerateArray().ToArray();
        Assert.Equal(new[] { Overview, UnityWork }, skills.Select(item => item.GetProperty("name").GetString()));
        Assert.All(skills, skill => Assert.False(skill.GetProperty("updated").GetBoolean()));
        Assert.All(
            skills,
            skill => Assert.StartsWith(
                Path.Combine(AppContext.BaseDirectory, "skills"),
                skill.GetProperty("sourcePath").GetString()!,
                StringComparison.Ordinal));

        var overviewRoot = Path.Combine(project.ProjectPath, ".agents", "skills", Overview);
        var unityRoot = Path.Combine(project.ProjectPath, ".agents", "skills", UnityWork);
        Assert.True(File.Exists(Path.Combine(overviewRoot, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(overviewRoot, "references", "troubleshooting.md")));
        Assert.True(File.Exists(Path.Combine(unityRoot, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(unityRoot, "references", "operation-contract.md")));
        Assert.True(File.Exists(Path.Combine(unityRoot, "references", "verification.md")));

        foreach (var name in new[] { Overview, UnityWork })
        {
            var markerPath = Path.Combine(project.ProjectPath, ".agents", "skills", name, ".bdtool-managed");
            Assert.True(File.Exists(markerPath), "缺少受管标记：" + markerPath);
            var marker = File.ReadAllText(markerPath);
            Assert.StartsWith("bdtool-managed-skill", marker, StringComparison.Ordinal);
            Assert.Contains("version=1", marker, StringComparison.Ordinal);
            Assert.Contains("name=" + name, marker, StringComparison.Ordinal);
            Assert.Contains("checksum=", marker, StringComparison.Ordinal);
        }

        Assert.False(Directory.Exists(Path.Combine(project.ProjectPath, ".bdtool")));
    }

    [Fact]
    public async Task Install_DefaultJson_ReturnsFullEnvelopeWithSkillInstallResultData()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, stderr) = await RunAsync(
            "skills", "install", "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        // 整个 stdout 必须是单个 JSON 文档，不能混入人类诊断。
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("errors").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("proof").ValueKind);

        var data = root.GetProperty("data");
        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.Equal("project", data.GetProperty("scope").GetString());
        Assert.Equal(JsonValueKind.String, data.GetProperty("projectPath").ValueKind);
        Assert.Equal(JsonValueKind.String, data.GetProperty("skillsRoot").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("skills").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("conflicts").ValueKind);

        foreach (var skill in data.GetProperty("skills").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, skill.GetProperty("name").ValueKind);
            Assert.Equal(JsonValueKind.String, skill.GetProperty("destinationPath").ValueKind);
            Assert.Equal(JsonValueKind.String, skill.GetProperty("sourcePath").ValueKind);
            Assert.Equal(JsonValueKind.False, skill.GetProperty("updated").ValueKind);
        }
    }

    [Fact]
    public async Task Install_SecondRun_ReportsUpdatedTrue()
    {
        using var project = new TemporaryProjectDirectory();

        var first = await RunAsync("skills", "install", "--project", project.ProjectPath);
        Assert.Equal(0, first.ExitCode);
        using (var firstDocument = JsonDocument.Parse(first.Stdout))
        {
            Assert.All(
                firstDocument.RootElement.GetProperty("data").GetProperty("skills").EnumerateArray(),
                skill => Assert.False(skill.GetProperty("updated").GetBoolean()));
        }

        var second = await RunAsync("skills", "install", "--project", project.ProjectPath);
        Assert.True(second.ExitCode == 0, $"stdout={second.Stdout}\nstderr={second.Stderr}");
        using var secondDocument = JsonDocument.Parse(second.Stdout);
        var skills = secondDocument.RootElement.GetProperty("data").GetProperty("skills").EnumerateArray().ToArray();
        Assert.Equal(2, skills.Length);
        Assert.All(skills, skill => Assert.True(skill.GetProperty("updated").GetBoolean()));
    }

    [Fact]
    public async Task Install_Conflict_DoesNotOverwriteOrPartiallyInstall()
    {
        using var project = new TemporaryProjectDirectory();
        var overviewRoot = Path.Combine(project.ProjectPath, ".agents", "skills", Overview);
        Directory.CreateDirectory(overviewRoot);
        var sentinel = Path.Combine(overviewRoot, "keep.txt");
        File.WriteAllText(sentinel, "keep me");

        var (exitCode, stdout, stderr) = await RunAsync(
            "skills", "install", "--project", project.ProjectPath);

        Assert.Equal(5, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("failed", root.GetProperty("status").GetString());
        var error = root.GetProperty("errors")[0];
        Assert.Equal("PRECONDITION_FAILED", error.GetProperty("code").GetString());

        var data = root.GetProperty("data");
        Assert.Empty(data.GetProperty("skills").EnumerateArray());
        var conflict = Assert.Single(data.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(Overview, conflict.GetProperty("name").GetString());
        Assert.Equal(overviewRoot, conflict.GetProperty("destinationPath").GetString());
        Assert.Contains(".bdtool-managed", conflict.GetProperty("reason").GetString(), StringComparison.Ordinal);

        Assert.Equal("keep me", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(overviewRoot, ".bdtool-managed")));
        Assert.False(File.Exists(Path.Combine(overviewRoot, "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(project.ProjectPath, ".agents", "skills", UnityWork)));
    }

    [Fact]
    public async Task Install_InvalidScope_ReturnsInvalidArgumentWithoutTouchingProject()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, _) = await RunAsync(
            "skills", "install", "--scope", "user", "--project", project.ProjectPath);

        Assert.Equal(2, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("INVALID_ARGUMENT", document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(project.ProjectPath, ".agents")));
    }

    [Fact]
    public async Task Install_HumanMode_WritesReadableOutput()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, stderr) = await RunAsync(
            "skills", "install", "--human", "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.StartsWith("status: completed", stdout, StringComparison.Ordinal);
        Assert.Contains("scope: project", stdout, StringComparison.Ordinal);
        Assert.Contains("skill: " + Overview + "\tinstalled", stdout, StringComparison.Ordinal);
        Assert.Contains("skill: " + UnityWork + "\tinstalled", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"data\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputSkillsFolder_IsCopiedWithReferences()
    {
        var skillsRoot = Path.Combine(AppContext.BaseDirectory, "skills");
        Assert.True(File.Exists(Path.Combine(skillsRoot, Overview, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(skillsRoot, Overview, "references", "troubleshooting.md")));
        Assert.True(File.Exists(Path.Combine(skillsRoot, UnityWork, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(skillsRoot, UnityWork, "references", "operation-contract.md")));
        Assert.True(File.Exists(Path.Combine(skillsRoot, UnityWork, "references", "verification.md")));
    }

    [Fact]
    public async Task Install_MissingProject_ReturnsProjectNotFoundExitFour()
    {
        using var project = new TemporaryProjectDirectory();
        var missing = Path.Combine(project.ProjectPath, "missing-project");

        var (exitCode, stdout, _) = await RunAsync(
            "skills", "install", "--project", missing);

        Assert.Equal(4, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("PROJECT_NOT_FOUND", document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task Install_UndefinedOrDuplicateOptions_ReturnInvalidArgument()
    {
        using var project = new TemporaryProjectDirectory();

        var unknownFlag = await RunAsync(
            "skills", "install", "--bad-option", "--project", project.ProjectPath);
        Assert.Equal(2, unknownFlag.ExitCode);
        using (var document = JsonDocument.Parse(unknownFlag.Stdout))
        {
            Assert.Equal("INVALID_ARGUMENT", document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        }

        // catalog/Unity 选项不属于 skills 命令，不能被静默忽略。
        var runtimeFlag = await RunAsync(
            "skills", "install", "--unity-cli", "x", "--project", project.ProjectPath);
        Assert.Equal(2, runtimeFlag.ExitCode);

        var duplicateScope = await RunAsync(
            "skills", "install", "--scope", "project", "--scope", "project", "--project", project.ProjectPath);
        Assert.Equal(2, duplicateScope.ExitCode);

        var missingSubcommand = await RunAsync("skills");
        Assert.Equal(2, missingSubcommand.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(project.ProjectPath, ".agents")));
    }

    [Fact]
    public void PublishedSkills_HaveStableTriggersAndOnlyCallBdtool()
    {
        var skillsRoot = Path.Combine(AppContext.BaseDirectory, "skills");
        var skillFiles = new[]
        {
            (Name: Overview, Path: Path.Combine(skillsRoot, Overview, "SKILL.md")),
            (Name: UnityWork, Path: Path.Combine(skillsRoot, UnityWork, "SKILL.md"))
        };
        var forbidden = new[]
        {
            "unity-cli",
            "BDTool.Provider.Akio",
            "BDTool.Provider.Local",
            "myunity"
        };

        var markdownFiles = Directory.GetFiles(skillsRoot, "*.md", SearchOption.AllDirectories);
        Assert.NotEmpty(markdownFiles);
        foreach (var markdownPath in markdownFiles)
        {
            var text = File.ReadAllText(markdownPath).Replace("\r\n", "\n");
            var lowered = text.ToLowerInvariant();
            Assert.All(forbidden, value => Assert.DoesNotContain(value.ToLowerInvariant(), lowered, StringComparison.Ordinal));

            // 文档中的每个 PowerShell 示例都只能走 BDTool 稳定 CLI，不能夹带底层命令。
            foreach (Match block in Regex.Matches(text, "```powershell\n(?<body>.*?)```", RegexOptions.Singleline))
            {
                var commands = block.Groups["body"].Value.Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal));
                Assert.All(commands, command => Assert.StartsWith("bdtool ", command, StringComparison.Ordinal));
            }
        }

        foreach (var skill in skillFiles)
        {
            var text = File.ReadAllText(skill.Path).Replace("\r\n", "\n");
            Assert.StartsWith("---\n", text, StringComparison.Ordinal);
            Assert.Contains("\nname: " + skill.Name + "\n", text, StringComparison.Ordinal);
            Assert.Contains("\ndescription: ", text, StringComparison.Ordinal);
        }

        var overviewText = File.ReadAllText(skillFiles[0].Path);
        var unityWorkText = File.ReadAllText(skillFiles[1].Path);
        Assert.Contains("普通软件问答", overviewText, StringComparison.Ordinal);
        Assert.Contains("普通 Unity 问答", unityWorkText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_ListsSkillsInstallCommand()
    {
        var (exitCode, stdout, stderr) = await RunAsync("--help");

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("bdtool skills install", stdout, StringComparison.Ordinal);
        Assert.Contains("--scope project", stdout, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await CliEntrypoint.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }
}
