using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Cli;
using BDTool.Contracts;
using BDTool.Tests.Support;

namespace BDTool.Tests.Cli;

/// <summary>
/// 通过可测试入口执行真实组合与真实临时项目，验收 M0 的 CLI 契约：
/// doctor 成功、未知/未支持操作留下 Activity、activity list/show 可查询，
/// 以及 --json 时 stdout 只包含单个 JSON 文档。
/// </summary>
public sealed class CliEntrypointTests
{
    [Fact]
    public void CatalogFile_IsCopiedNextToTestBinary()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "catalog", "operations.json")));
    }

    [Fact]
    public async Task DoctorJson_ReturnsReadyReportWithPureStdout()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, stderr) = await RunAsync(
            "doctor", "--json", "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        // JsonDocument.Parse 会拒绝 JSON 之后的额外内容，因此解析成功即证明 stdout 纯净。
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("completed", root.GetProperty("status").GetString());

        var data = root.GetProperty("data");
        Assert.Equal("ready", data.GetProperty("state").GetString());
        Assert.Equal("0.1.0", data.GetProperty("appVersion").GetString());
        Assert.Equal(BDToolContract.Version, data.GetProperty("contractVersion").GetString());
        Assert.Equal(Path.GetFullPath(project.ProjectPath), data.GetProperty("projectPath").GetString());
        Assert.Equal(0, data.GetProperty("providers").GetArrayLength());

        var checks = data.GetProperty("checks").EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("id").GetString()!,
                item => item.GetProperty("status").GetString()!);
        foreach (var checkId in new[]
                 {
                     "app.version",
                     "contract.version",
                     "project.path",
                     "state.root",
                     "activity.root",
                     "catalog.loaded",
                     "providers.empty"
                 })
        {
            Assert.Equal("pass", checks[checkId]);
        }

        Assert.Equal("skipped", checks["unity.connection"]);
    }

    [Fact]
    public async Task DoctorJson_SucceedsWithoutAnyUnityIntegration()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, _) = await RunAsync(
            "doctor", "--json", "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);

        using var document = JsonDocument.Parse(stdout);
        var data = document.RootElement.GetProperty("data");
        var unity = data.GetProperty("unity");
        Assert.False(unity.GetProperty("connected").GetBoolean());
        Assert.Equal("not-implemented", unity.GetProperty("integration").GetString());
        Assert.Equal(0, data.GetProperty("providers").GetArrayLength());
    }

    [Fact]
    public async Task Doctor_WritesActivityFileUnderProjectStateDirectory()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, _, stderr) = await RunAsync(
            "doctor", "--json", "--project", project.ProjectPath);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        var file = Assert.Single(FindActivityFiles(project));
        // 权威布局：<project>/.bdtool/state/activity/<yyyyMMdd>/<activityId>.json
        Assert.Equal(
            Path.GetFullPath(project.ActivityRoot),
            Path.GetFullPath(Path.GetDirectoryName(Path.GetDirectoryName(file)!)!));

        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var root = document.RootElement;
        var activityId = root.GetProperty("activityId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(activityId));
        Assert.Equal(Path.GetFileNameWithoutExtension(file), activityId);
        Assert.Equal(activityId, root.GetProperty("traceId").GetString());
        Assert.Equal("cli", root.GetProperty("source").GetString());
        Assert.Equal("system.doctor", root.GetProperty("operationId").GetString());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("startedAt").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("finishedAt").GetString()));
    }

    [Fact]
    public async Task UnknownOperation_ReturnsExitCodeTwoAndWritesFailedActivity()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, stderr) = await RunAsync(
            "nope.nope", "--json", "--project", project.ProjectPath);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = JsonDocument.Parse(stdout);
        var error = document.RootElement.GetProperty("errors")[0];
        Assert.Equal("NOT_FOUND", error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(JsonValueKind.Object, error.GetProperty("details").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));

        using var record = ReadSingleActivity(project);
        var root = record.RootElement;
        var recordActivityId = root.GetProperty("activityId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(recordActivityId));
        Assert.Equal(recordActivityId, root.GetProperty("traceId").GetString());
        Assert.Equal("cli", root.GetProperty("source").GetString());
        Assert.Equal("nope.nope", root.GetProperty("operationId").GetString());
        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("exitCode").GetInt32());
        var recordError = root.GetProperty("error");
        Assert.Equal("NOT_FOUND", recordError.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(recordError.GetProperty("message").GetString()));
        Assert.False(recordError.GetProperty("retryable").GetBoolean());
        Assert.Equal(JsonValueKind.Object, recordError.GetProperty("details").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("startedAt").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("finishedAt").GetString()));
    }

    [Fact]
    public async Task UnsupportedOperation_ReturnsExitCodeThreeAndWritesUnsupportedActivity()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, _) = await RunAsync(
            "operations.list", "--json", "--project", project.ProjectPath);

        Assert.Equal(3, exitCode);

        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("unsupported", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "CAPABILITY_UNSUPPORTED",
            document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());

        using var record = ReadSingleActivity(project);
        var root = record.RootElement;
        var recordActivityId = root.GetProperty("activityId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(recordActivityId));
        Assert.Equal(recordActivityId, root.GetProperty("traceId").GetString());
        Assert.Equal("cli", root.GetProperty("source").GetString());
        Assert.Equal("operations.list", root.GetProperty("operationId").GetString());
        Assert.Equal("unsupported", root.GetProperty("status").GetString());
        Assert.Equal(3, root.GetProperty("exitCode").GetInt32());
        var recordError = root.GetProperty("error");
        Assert.Equal("CAPABILITY_UNSUPPORTED", recordError.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(recordError.GetProperty("message").GetString()));
        Assert.False(recordError.GetProperty("retryable").GetBoolean());
        Assert.Equal(JsonValueKind.Object, recordError.GetProperty("details").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("startedAt").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("finishedAt").GetString()));
    }

    [Fact]
    public async Task ActivityList_Json_ReturnsRecordedOperation()
    {
        using var project = new TemporaryProjectDirectory();
        await RunAsync("doctor", "--json", "--project", project.ProjectPath);

        var (exitCode, stdout, stderr) = await RunAsync(
            "activity", "list", "--json", "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
        var item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("activityId").GetString()));
        Assert.Equal("system.doctor", item.GetProperty("operationId").GetString());
        Assert.Equal("completed", item.GetProperty("status").GetString());
        Assert.Equal("cli", item.GetProperty("source").GetString());
    }

    [Fact]
    public async Task ActivityList_EmptyProject_ReturnsZeroRecords()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, _) = await RunAsync(
            "activity", "list", "--json", "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task ActivityShow_Json_ReturnsFullRecordWithMatchingTraceId()
    {
        using var project = new TemporaryProjectDirectory();
        await RunAsync("doctor", "--json", "--project", project.ProjectPath);
        var activityId = ReadActivityId(project);

        var (exitCode, stdout, stderr) = await RunAsync(
            "activity", "show", activityId, "--json", "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal(activityId, root.GetProperty("activityId").GetString());
        Assert.Equal(activityId, root.GetProperty("traceId").GetString());
        Assert.Equal("system.doctor", root.GetProperty("operationId").GetString());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("input").GetProperty("previewJson").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("output").GetProperty("previewJson").GetString()));
    }

    [Fact]
    public async Task ActivityShow_HumanMode_WritesRecordToStdout()
    {
        using var project = new TemporaryProjectDirectory();
        await RunAsync("doctor", "--json", "--project", project.ProjectPath);
        var activityId = ReadActivityId(project);

        var (exitCode, stdout, _) = await RunAsync(
            "activity", "show", activityId, "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);
        Assert.Contains(activityId, stdout, StringComparison.Ordinal);
        Assert.Contains("system.doctor", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivityShow_UnknownId_ReturnsExitCodeTwo()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, _) = await RunAsync(
            "activity", "show", "doesnotexist", "--json", "--project", project.ProjectPath);

        Assert.Equal(2, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("NOT_FOUND", document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Version_PrintsProductAndExitCodeZero()
    {
        var (exitCode, stdout, stderr) = await RunAsync("--version");

        Assert.Equal(0, exitCode);
        Assert.Contains("bdtool 0.1.0", stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public async Task Help_PrintsUsage()
    {
        var (exitCode, stdout, _) = await RunAsync("--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("用法", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoArguments_IsInvalidInvocation()
    {
        var (exitCode, _, stderr) = await RunAsync();

        Assert.Equal(2, exitCode);
        Assert.Contains("用法", stderr, StringComparison.Ordinal);
    }

    /// <summary>枚举项目内的 Activity 文件；目录缺失时视为装配缺陷直接失败。</summary>
    private static string[] FindActivityFiles(TemporaryProjectDirectory project)
    {
        Assert.True(
            Directory.Exists(project.ActivityRoot),
            "未在项目下创建 .bdtool/state/activity 目录。");
        return Directory.GetFiles(project.ActivityRoot, "*.json", SearchOption.AllDirectories);
    }

    /// <summary>读取唯一一条 Activity 文件，用于断言落盘字段。</summary>
    private static JsonDocument ReadSingleActivity(TemporaryProjectDirectory project)
    {
        var file = Assert.Single(FindActivityFiles(project));
        return JsonDocument.Parse(File.ReadAllText(file));
    }

    /// <summary>取唯一一条活动的 activityId，供 show 子命令复用。</summary>
    private static string ReadActivityId(TemporaryProjectDirectory project)
    {
        using var document = ReadSingleActivity(project);
        return document.RootElement.GetProperty("activityId").GetString()!;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await CliEntrypoint.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }
}
