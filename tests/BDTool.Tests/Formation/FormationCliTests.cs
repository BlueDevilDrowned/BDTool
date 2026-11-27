using System.Text.Json;
using BDTool.Cli;

namespace BDTool.Tests.Formation;

/// <summary>
/// 真实 CLI 入口的 formation 路由测试：自动 requestId、统一信封、退出码、
/// 回执投影以及“formation mutation 不写 Activity”的跨模块约束。
/// </summary>
public sealed class FormationCliTests
{
    [Fact]
    public async Task FormationCommands_SupportAutoRequestIdJsonHumanAndExitCodes()
    {
        using var project = new FormationTestProject();

        var created = await RunAsync(
            project,
            "formation", "create",
            "--id", "formation-cli",
            "--objective", "CLI 编队",
            "--request-id", "auto");
        Assert.Equal(0, created.ExitCode);
        using var createdJson = JsonDocument.Parse(created.Stdout);
        var createdRoot = createdJson.RootElement;
        Assert.Equal("completed", createdRoot.GetProperty("status").GetString());
        var createdData = createdRoot.GetProperty("data");
        Assert.False(string.IsNullOrWhiteSpace(createdData.GetProperty("requestId").GetString()));
        Assert.False(createdData.GetProperty("replayed").GetBoolean());
        Assert.Equal(
            "formation-cli",
            createdData.GetProperty("formation").GetProperty("id").GetString());

        Assert.Equal(0, (await RunAsync(
            project,
            "formation", "seat", "add",
            "--formation", "formation-cli",
            "--id", "executor",
            "--role", "Executor",
            "--display-name", "执行者",
            "--access", "write",
            "--write-path", "Assets/CLI",
            "--request-id", "cli-seat-executor")).ExitCode);
        Assert.Equal(0, (await RunAsync(
            project,
            "formation", "seat", "add",
            "--formation", "formation-cli",
            "--id", "reviewer",
            "--role", "Reviewer",
            "--display-name", "审核者",
            "--request-id", "cli-seat-reviewer")).ExitCode);
        Assert.Equal(0, (await RunAsync(
            project,
            "formation", "task", "create",
            "--formation", "formation-cli",
            "--id", "task-cli",
            "--title", "CLI 任务",
            "--objective", "测试 CLI",
            "--instructions", "只读",
            "--acceptance", "命令成功",
            "--executor-seat", "executor",
            "--reviewer-seat", "reviewer",
            "--access", "read",
            "--request-id", "cli-task-create")).ExitCode);

        var firstDispatch = await RunAsync(
            project,
            "formation", "task", "dispatch",
            "--formation", "formation-cli",
            "--task", "task-cli",
            "--request-id", "cli-dispatch-1");
        Assert.Equal(0, firstDispatch.ExitCode);
        var secondDispatch = await RunAsync(
            project,
            "formation", "task", "dispatch",
            "--formation", "formation-cli",
            "--task", "task-cli",
            "--request-id", "cli-dispatch-2");
        Assert.Equal(5, secondDispatch.ExitCode);
        using (var failedJson = JsonDocument.Parse(secondDispatch.Stdout))
        {
            Assert.Equal(
                "PRECONDITION_FAILED",
                failedJson.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        }

        var show = await RunAsync(project, "formation", "show", "formation-cli");
        Assert.Equal(0, show.ExitCode);
        using (var showJson = JsonDocument.Parse(show.Stdout))
        {
            var receipts = showJson.RootElement
                .GetProperty("data")
                .GetProperty("requestReceipts");
            Assert.Equal(JsonValueKind.Object, receipts.ValueKind);
            Assert.Empty(receipts.EnumerateObject());
        }

        var showWithReceipts = await RunAsync(
            project,
            "formation", "show", "formation-cli",
            "--include-receipts");
        Assert.Equal(0, showWithReceipts.ExitCode);
        using (var receiptsJson = JsonDocument.Parse(showWithReceipts.Stdout))
        {
            Assert.True(receiptsJson.RootElement
                .GetProperty("data")
                .GetProperty("requestReceipts")
                .EnumerateObject()
                .Any());
        }

        var invalidReview = await RunAsync(
            project,
            "formation", "task", "review",
            "--formation", "formation-cli",
            "--task", "task-cli",
            "--submission", "missing",
            "--request-id", "cli-review-invalid");
        Assert.Equal(2, invalidReview.ExitCode);

        var humanList = await RunAsync(
            project,
            "formation", "list",
            "--human");
        Assert.Equal(0, humanList.ExitCode);
        Assert.Contains("formation-cli", humanList.Stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, humanList.Stderr);

        var activityRoot = Path.Combine(
            project.ProjectPath,
            ".bdtool",
            "state",
            "activity");
        var activityFiles = Directory.Exists(activityRoot)
            ? Directory.GetFiles(activityRoot, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        Assert.Empty(activityFiles);
    }

    [Fact]
    public async Task NativeCommands_EmitLeafSpawnRequestAndUseUnsupportedExitCodes()
    {
        using var project = new FormationTestProject();
        await AssertSuccessAsync(RunAsync(
            project,
            "formation", "create",
            "--id", "formation-native-cli",
            "--objective", "原生派发",
            "--budget-json", "{\"subagentMax\":1,\"maxDepth\":2}",
            "--request-id", "native-cli-create"));
        await AssertSuccessAsync(RunAsync(
            project,
            "formation", "seat", "add",
            "--formation", "formation-native-cli",
            "--id", "executor",
            "--role", "Executor",
            "--display-name", "执行者",
            "--dispatch-mode", "native",
            "--access", "read",
            "--request-id", "native-cli-executor"));
        await AssertSuccessAsync(RunAsync(
            project,
            "formation", "seat", "add",
            "--formation", "formation-native-cli",
            "--id", "reviewer",
            "--role", "Reviewer",
            "--display-name", "审核者",
            "--access", "read",
            "--request-id", "native-cli-reviewer"));
        await AssertSuccessAsync(RunAsync(
            project,
            "formation", "task", "create",
            "--formation", "formation-native-cli",
            "--id", "task-native-cli",
            "--title", "只读检查",
            "--objective", "返回结果",
            "--instructions", "禁止创建子智能体",
            "--acceptance", "证据有效",
            "--executor-seat", "executor",
            "--reviewer-seat", "reviewer",
            "--access", "read",
            "--request-id", "native-cli-task"));

        var prepared = await RunAsync(
            project,
            "formation", "task", "native-prepare",
            "--formation", "formation-native-cli",
            "--task", "task-native-cli",
            "--request-id", "native-cli-prepare");
        Assert.Equal(0, prepared.ExitCode);
        using (var preparedJson = JsonDocument.Parse(prepared.Stdout))
        {
            var spawn = preparedJson.RootElement
                .GetProperty("data")
                .GetProperty("spawnRequest");
            Assert.True(spawn.GetProperty("canSpawn").GetBoolean());
            Assert.True(spawn.GetProperty("leafOnly").GetBoolean());
            Assert.Equal(0, spawn.GetProperty("maxSubagents").GetInt32());
            Assert.Equal(1, spawn.GetProperty("maxDepth").GetInt32());
        }

        var unsupported = await RunAsync(
            project,
            "formation", "task", "unsupported",
            "--formation", "formation-native-cli",
            "--task", "task-native-cli",
            "--reason", "host-tool-missing",
            "--request-id", "native-cli-unsupported");
        Assert.Equal(3, unsupported.ExitCode);
        using (var unsupportedJson = JsonDocument.Parse(unsupported.Stdout))
        {
            Assert.Equal(
                "CAPABILITY_UNSUPPORTED",
                unsupportedJson.RootElement.GetProperty("errors")[0]
                    .GetProperty("code")
                    .GetString());
        }

        await AssertSuccessAsync(RunAsync(
            project,
            "formation", "create",
            "--id", "formation-unknown-cli",
            "--objective", "未知结果",
            "--request-id", "unknown-cli-create"));
        await AssertSuccessAsync(RunAsync(
            project,
            "formation", "seat", "add",
            "--formation", "formation-unknown-cli",
            "--id", "executor",
            "--role", "Executor",
            "--display-name", "执行者",
            "--dispatch-mode", "native",
            "--access", "read",
            "--request-id", "unknown-cli-executor"));
        await AssertSuccessAsync(RunAsync(
            project,
            "formation", "seat", "add",
            "--formation", "formation-unknown-cli",
            "--id", "reviewer",
            "--role", "Reviewer",
            "--display-name", "审核者",
            "--access", "read",
            "--request-id", "unknown-cli-reviewer"));
        await AssertSuccessAsync(RunAsync(
            project,
            "formation", "task", "create",
            "--formation", "formation-unknown-cli",
            "--id", "task-unknown-cli",
            "--title", "未知结果",
            "--objective", "测试未知",
            "--instructions", "只读",
            "--acceptance", "状态可追踪",
            "--executor-seat", "executor",
            "--reviewer-seat", "reviewer",
            "--access", "read",
            "--request-id", "unknown-cli-task"));
        await AssertSuccessAsync(RunAsync(
            project,
            "formation", "task", "native-prepare",
            "--formation", "formation-unknown-cli",
            "--task", "task-unknown-cli",
            "--request-id", "unknown-cli-prepare"));

        var deliveryUnknown = await RunAsync(
            project,
            "formation", "task", "delivery-unknown",
            "--formation", "formation-unknown-cli",
            "--task", "task-unknown-cli",
            "--reason", "agent-process-disappeared",
            "--request-id", "unknown-cli-result");
        Assert.Equal(8, deliveryUnknown.ExitCode);
    }

    [Fact]
    public async Task AgentProfiles_InstallAtomicallyAndPreserveUnmanagedFiles()
    {
        using var project = new FormationTestProject();
        var installed = await RunAsync(project, "agents", "install");
        Assert.Equal(0, installed.ExitCode);
        using (var json = JsonDocument.Parse(installed.Stdout))
        {
            var profiles = json.RootElement
                .GetProperty("data")
                .GetProperty("profiles");
            Assert.Equal(4, profiles.GetArrayLength());
        }

        var agentsRoot = Path.Combine(project.ProjectPath, ".codex", "agents");
        Assert.Equal(4, Directory.GetFiles(agentsRoot, "*.toml").Length);
        Assert.All(
            Directory.GetFiles(agentsRoot, "*.toml"),
            path => Assert.StartsWith(
                "# bdtool-managed: agent-profile-v1",
                File.ReadAllText(path),
                StringComparison.Ordinal));

        using var conflictProject = new FormationTestProject();
        var conflictRoot = Path.Combine(conflictProject.ProjectPath, ".codex", "agents");
        Directory.CreateDirectory(conflictRoot);
        var unmanaged = Path.Combine(conflictRoot, "bdtool-reviewer.toml");
        await File.WriteAllTextAsync(unmanaged, "name = \"user-owned\"\n");
        var conflict = await RunAsync(conflictProject, "agents", "install");
        Assert.Equal(5, conflict.ExitCode);
        Assert.Equal(
            "name = \"user-owned\"\n",
            await File.ReadAllTextAsync(unmanaged));
        Assert.Single(Directory.GetFiles(conflictRoot, "*.toml"));
    }

    [Fact]
    public async Task FormationCli_InfersWriteAccessAndNormalizesRoleCasing()
    {
        using var project = new FormationTestProject();

        Assert.Equal(0, (await RunAsync(
            project,
            "formation", "create",
            "--id", "cli-defaults",
            "--objective", "默认值",
            "--request-id", "defaults-create")).ExitCode);

        // 小写 role + 仅给 write-path 时，CLI 应归一化角色并推断 write 访问。
        Assert.Equal(0, (await RunAsync(
            project,
            "formation", "seat", "add",
            "--formation", "cli-defaults",
            "--id", "executor",
            "--role", "executor",
            "--display-name", "执行者",
            "--write-path", "Assets/Core",
            "--request-id", "defaults-seat-executor")).ExitCode);
        Assert.Equal(0, (await RunAsync(
            project,
            "formation", "seat", "add",
            "--formation", "cli-defaults",
            "--id", "reviewer",
            "--role", "reviewer",
            "--display-name", "审核者",
            "--request-id", "defaults-seat-reviewer")).ExitCode);
        Assert.Equal(0, (await RunAsync(
            project,
            "formation", "task", "create",
            "--formation", "cli-defaults",
            "--id", "task",
            "--title", "任务",
            "--objective", "目标",
            "--instructions", "步骤",
            "--acceptance", "通过",
            "--executor-seat", "executor",
            "--reviewer-seat", "reviewer",
            "--write-path", "Assets/Core",
            "--request-id", "defaults-task-create")).ExitCode);

        var show = await RunAsync(project, "formation", "show", "cli-defaults");
        Assert.Equal(0, show.ExitCode);
        using var json = JsonDocument.Parse(show.Stdout);
        var formation = json.RootElement.GetProperty("data");
        var executor = formation.GetProperty("seats")[0];
        Assert.Equal("Executor", executor.GetProperty("role").GetString());
        Assert.Equal("write", executor.GetProperty("workspace").GetProperty("access").GetString());
        Assert.Equal(
            "write",
            formation.GetProperty("tasks")[0].GetProperty("workspace").GetProperty("access").GetString());
    }
    private static async Task<CliRunResult> RunAsync(
        FormationTestProject project,
        params string[] arguments)
    {
        var args = arguments
            .Concat(new[] { "--project", project.ProjectPath })
            .ToArray();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await CliEntrypoint.RunAsync(
            args,
            stdout,
            stderr,
            CancellationToken.None);
        return new CliRunResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task AssertSuccessAsync(Task<CliRunResult> pending)
    {
        var result = await pending;
        Assert.True(
            result.ExitCode == 0,
            $"exit={result.ExitCode}\nstdout={result.Stdout}\nstderr={result.Stderr}");
    }

    private readonly record struct CliRunResult(
        int ExitCode,
        string Stdout,
        string Stderr);
}
