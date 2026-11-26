using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Cli;
using BDTool.Contracts;
using BDTool.Tests.Support;

namespace BDTool.Tests.Cli;

/// <summary>
/// 通过可测试入口执行真实组合与真实临时项目，验收 M1 的 CLI 契约：
/// 默认 JSON、--human、--input/--input-json、operations/context/workspace 路由、
/// Activity 审计与稳定退出码。所有用例都使用真实文件系统，不伪造 Provider 结果。
/// </summary>
public sealed class CliEntrypointTests
{
    [Fact]
    public void CatalogFile_IsCopiedNextToTestBinary()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "catalog", "operations.json")));
    }

    [Fact]
    public async Task Doctor_DefaultOutputIsJsonAndReportsLocalProvider()
    {
        using var project = new TemporaryProjectDirectory();

        // 省略 --json：默认仍必须是单个 JSON 文档。
        var (exitCode, stdout, stderr) = await RunAsync("doctor", "--project", project.ProjectPath);

        Assert.True(exitCode == 0, $"exit={exitCode}\nstdout={stdout}\nstderr={stderr}");
        Assert.Equal(string.Empty, stderr);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("completed", root.GetProperty("status").GetString());

        var data = root.GetProperty("data");
        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.Equal("ready", data.GetProperty("state").GetString());
        Assert.Equal("M1", data.GetProperty("milestone").GetString());
        Assert.Equal("0.1.0", data.GetProperty("appVersion").GetString());
        Assert.Equal(BDToolContract.Version, data.GetProperty("contractVersion").GetString());
        Assert.Equal(Path.GetFullPath(project.ProjectPath), data.GetProperty("projectPath").GetString());

        var provider = Assert.Single(data.GetProperty("providers").EnumerateArray());
        Assert.Equal("local", provider.GetProperty("id").GetString());
        Assert.Equal("workspace", provider.GetProperty("kind").GetString());

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
                     "providers.available"
                 })
        {
            Assert.Equal("pass", checks[checkId]);
        }

        Assert.Equal("skipped", checks["unity.connection"]);
        var unity = data.GetProperty("unity");
        Assert.False(unity.GetProperty("connected").GetBoolean());
        Assert.Equal("not-implemented", unity.GetProperty("integration").GetString());
    }

    [Fact]
    public async Task Doctor_ExplicitJsonIsIdempotent()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, stderr) = await RunAsync(
            "doctor", "--json", "--json", "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("ready", document.RootElement.GetProperty("data").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Doctor_HumanMode_WritesTextInsteadOfJson()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, stderr) = await RunAsync(
            "doctor", "--human", "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("state: ready", stdout, StringComparison.Ordinal);
        Assert.Contains("providers: local", stdout, StringComparison.Ordinal);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(stdout));
    }

    [Fact]
    public async Task Doctor_WritesActivityFileUnderProjectStateDirectory()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, _, stderr) = await RunAsync(
            "doctor", "--project", project.ProjectPath);
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
        // 未知操作属于非 typed 失败：data 必须是空对象而不是 null，且保留实际错误信息。
        AssertEmptyObjectData(document.RootElement);
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
        Assert.Equal("NOT_FOUND", root.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task OperationsList_RoutesThroughGatewayAndWritesActivity()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, stderr) = await RunAsync(
            "operations", "list", "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("completed", root.GetProperty("status").GetString());
        var operations = root.GetProperty("data").GetProperty("operations").EnumerateArray().ToArray();
        Assert.Equal(8, operations.Length);
        Assert.Contains(operations, item => item.GetProperty("id").GetString() == "workspace.read");
        Assert.All(operations, item => Assert.True(item.GetProperty("implemented").GetBoolean()));

        // operations.list 不得在解析阶段直接读取目录而绕过审计。
        using var record = ReadSingleActivity(project);
        Assert.Equal("operations.list", record.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("completed", record.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OperationsDescribe_ReturnsTypedDetailAndWritesActivity()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, stderr) = await RunAsync(
            "operations", "describe", "workspace.read", "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = JsonDocument.Parse(stdout);
        var operation = document.RootElement.GetProperty("data").GetProperty("operation");
        Assert.Equal("workspace.read", operation.GetProperty("id").GetString());
        Assert.Equal("read", operation.GetProperty("effect").GetString());
        Assert.Equal("available", operation.GetProperty("availability").GetString());
        Assert.Equal(
            new[] { "workspace.read" },
            operation.GetProperty("requiredCapabilities").EnumerateArray()
                .Select(item => item.GetString()).ToArray());
        Assert.Contains("relativePath", operation.GetProperty("inputSchemaJson").GetString(), StringComparison.Ordinal);

        using var record = ReadSingleActivity(project);
        Assert.Equal("operations.describe", record.RootElement.GetProperty("operationId").GetString());
    }

    [Fact]
    public async Task OperationsDescribe_UnknownId_ReturnsNotFoundAndAudits()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, _) = await RunAsync(
            "operations", "describe", "missing.operation", "--project", project.ProjectPath);

        Assert.Equal(2, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("NOT_FOUND", document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal("missing.operation", document.RootElement.GetProperty("errors")[0]
            .GetProperty("details").GetProperty("operationId").GetString());

        using var record = ReadSingleActivity(project);
        Assert.Equal("operations.describe", record.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("failed", record.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, record.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Context_WithoutUnity_ReportsProjectVersionAndLocalProvider()
    {
        using var project = new TemporaryProjectDirectory();
        WriteProjectVersion(project, "2022.3.10f1");

        var (exitCode, stdout, stderr) = await RunAsync(
            "context", "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = JsonDocument.Parse(stdout);
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("projectExists").GetBoolean());
        Assert.Equal("2022.3.10f1", data.GetProperty("unityVersion").GetString());
        Assert.False(data.GetProperty("editor").GetProperty("connected").GetBoolean());
        Assert.Equal("not-connected", data.GetProperty("editor").GetProperty("status").GetString());
        var provider = Assert.Single(data.GetProperty("providers").EnumerateArray());
        Assert.Equal("local", provider.GetProperty("id").GetString());

        using var record = ReadSingleActivity(project);
        Assert.Equal("project.context", record.RootElement.GetProperty("operationId").GetString());
    }

    [Fact]
    public async Task Context_MissingProject_ReturnsExitCodeFour()
    {
        using var project = new TemporaryProjectDirectory();
        var missing = Path.Combine(project.ProjectPath, "missing-project");

        var (exitCode, stdout, _) = await RunAsync(
            "context", "--project", missing);

        Assert.Equal(4, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("PROJECT_NOT_FOUND", document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task RunWorkspaceRead_FileInput_StripsBomAndHashesRawBytes()
    {
        using var project = new TemporaryProjectDirectory();
        const string content = "line1\r\n中文\r\n";
        var rawBytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes(content))
            .ToArray();
        WriteBytes(project, "Assets/Sample.cs", rawBytes);

        var inputFile = WriteInputFile(project, "{\"relativePath\":\"Assets/Sample.cs\"}");
        var (exitCode, stdout, stderr) = await RunAsync(
            "run", "workspace.read", "--input", inputFile, "--project", project.ProjectPath);

        Assert.True(exitCode == 0, $"exit={exitCode}\nstdout={stdout}\nstderr={stderr}");
        Assert.Equal(string.Empty, stderr);

        using var document = JsonDocument.Parse(stdout);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("Assets/Sample.cs", data.GetProperty("relativePath").GetString());
        Assert.Equal(content, data.GetProperty("content").GetString());
        Assert.Equal(Encoding.UTF8.GetByteCount(content), data.GetProperty("length").GetInt64());
        Assert.Equal(Sha256(rawBytes), data.GetProperty("sha256").GetString());

        using var record = ReadSingleActivity(project);
        Assert.Equal("workspace.read", record.RootElement.GetProperty("operationId").GetString());
    }

    [Fact]
    public async Task RunWorkspaceSearch_InputJson_ExcludesIgnoredDirectoriesAndReportsTruncation()
    {
        using var project = new TemporaryProjectDirectory();
        WriteText(project, "Assets/a.txt", "needle here\n");
        WriteText(project, "Assets/b.txt", "another needle\n");
        WriteText(project, "bin/ignored.txt", "needle in bin\n");
        WriteText(project, ".bdtool/state/ignored.txt", "needle in state\n");

        var (exitCode, stdout, _) = await RunAsync(
            "run", "workspace.search",
            "--input-json", "{\"query\":\"needle\",\"maxResults\":10}",
            "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);
        using var first = JsonDocument.Parse(stdout);
        var data = first.RootElement.GetProperty("data");
        var matches = data.GetProperty("matches").EnumerateArray().ToArray();
        Assert.Equal(2, matches.Length);
        Assert.All(matches, item =>
            Assert.StartsWith("Assets/", item.GetProperty("relativePath").GetString(), StringComparison.Ordinal));
        Assert.False(data.GetProperty("truncated").GetBoolean());

        var (limitedExit, limitedStdout, _) = await RunAsync(
            "run", "workspace.search",
            "--input-json", "{\"query\":\"needle\",\"maxResults\":1}",
            "--project", project.ProjectPath);

        Assert.Equal(0, limitedExit);
        using var limited = JsonDocument.Parse(limitedStdout);
        Assert.Single(limited.RootElement.GetProperty("data").GetProperty("matches").EnumerateArray());
        Assert.True(limited.RootElement.GetProperty("data").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task RunWorkspaceFingerprint_InputJson_ReportsExists()
    {
        using var project = new TemporaryProjectDirectory();
        WriteText(project, "Assets/a.txt", "content\n");

        var (exitCode, stdout, _) = await RunAsync(
            "run", "workspace.fingerprint",
            "--input-json", "{\"relativePaths\":[\"Assets/a.txt\",\"missing.txt\"]}",
            "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var files = document.RootElement.GetProperty("data").GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(2, files.Length);
        Assert.True(files[0].GetProperty("exists").GetBoolean());
        Assert.False(files[1].GetProperty("exists").GetBoolean());
        Assert.Equal(string.Empty, files[1].GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task RunWorkspaceApply_CreatesUpdatesAndRejectsStaleCasWithoutSideEffects()
    {
        using var project = new TemporaryProjectDirectory();

        var (createExit, createStdout, _) = await RunAsync(
            "run", "workspace.apply",
            "--input-json", "{\"relativePath\":\"Notes/new.txt\",\"content\":\"hello\",\"createIfMissing\":true}",
            "--project", project.ProjectPath);

        Assert.Equal(0, createExit);
        using (var created = JsonDocument.Parse(createStdout))
        {
            var data = created.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("created").GetBoolean());
            Assert.True(data.GetProperty("changed").GetBoolean());
            Assert.False(data.GetProperty("dryRun").GetBoolean());
        }

        Assert.Equal("hello", File.ReadAllText(Path.Combine(project.ProjectPath, "Notes", "new.txt")));
        var helloHash = Sha256(Encoding.UTF8.GetBytes("hello"));

        var updateInput = "{\"relativePath\":\"Notes/new.txt\",\"content\":\"hello2\",\"expectedSha256\":\""
            + helloHash + "\"}";
        var (updateExit, updateStdout, _) = await RunAsync(
            "run", "workspace.apply", "--input-json", updateInput, "--project", project.ProjectPath);

        Assert.Equal(0, updateExit);
        using (var updated = JsonDocument.Parse(updateStdout))
        {
            Assert.Equal(helloHash, updated.RootElement.GetProperty("data").GetProperty("previousSha256").GetString());
            Assert.False(updated.RootElement.GetProperty("data").GetProperty("created").GetBoolean());
        }

        var staleInput = "{\"relativePath\":\"Notes/new.txt\",\"content\":\"should-not-write\","
            + "\"expectedSha256\":\"0000000000000000000000000000000000000000000000000000000000000000\"}";
        var (staleExit, staleStdout, _) = await RunAsync(
            "run", "workspace.apply", "--input-json", staleInput, "--project", project.ProjectPath);

        Assert.Equal(5, staleExit);
        using (var stale = JsonDocument.Parse(staleStdout))
        {
            Assert.Equal("PRECONDITION_FAILED", stale.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        }

        Assert.Equal("hello2", File.ReadAllText(Path.Combine(project.ProjectPath, "Notes", "new.txt")));

        var hello2Hash = Sha256(Encoding.UTF8.GetBytes("hello2"));
        var dryRunInput = "{\"relativePath\":\"Notes/new.txt\",\"content\":\"hello3\",\"expectedSha256\":\""
            + hello2Hash + "\",\"dryRun\":true}";
        var (dryRunExit, dryRunStdout, _) = await RunAsync(
            "run", "workspace.apply", "--input-json", dryRunInput, "--project", project.ProjectPath);

        Assert.Equal(0, dryRunExit);
        using (var dryRun = JsonDocument.Parse(dryRunStdout))
        {
            Assert.True(dryRun.RootElement.GetProperty("data").GetProperty("dryRun").GetBoolean());
            // dry-run 的 changed 表示“按计划将会改变”，目标文件必须保持原样。
            Assert.True(dryRun.RootElement.GetProperty("data").GetProperty("changed").GetBoolean());
        }

        Assert.Equal("hello2", File.ReadAllText(Path.Combine(project.ProjectPath, "Notes", "new.txt")));
    }

    [Fact]
    public async Task InvalidArguments_WriteJsonEnvelopeToStdoutAndExitTwo()
    {
        using var project = new TemporaryProjectDirectory();

        var (exitCode, stdout, stderr) = await RunAsync(
            "run", "workspace.read", "--project", project.ProjectPath, "--input");

        Assert.Equal(2, exitCode);
        using var document = JsonDocument.Parse(stdout);
        // 参数解析失败没有 typed DTO，但信封仍必须带 data 对象，避免调用方需要区分 null 与缺失。
        AssertEmptyObjectData(document.RootElement);
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("INVALID_ARGUMENT", document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Contains("bdtool:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingInputFile_ReturnsInvalidArgument()
    {
        using var project = new TemporaryProjectDirectory();
        var missing = Path.Combine(project.ProjectPath, "no-such-input.json");

        var (exitCode, stdout, _) = await RunAsync(
            "run", "workspace.read", "--input", missing, "--project", project.ProjectPath);

        Assert.Equal(2, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("INVALID_ARGUMENT", document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvalidInlineJsonAndMixedInputOptions_ReturnInvalidArgument()
    {
        using var project = new TemporaryProjectDirectory();

        var (invalidExit, invalidStdout, _) = await RunAsync(
            "run", "workspace.read", "--input-json", "{", "--project", project.ProjectPath);
        Assert.Equal(2, invalidExit);
        using (var invalid = JsonDocument.Parse(invalidStdout))
        {
            Assert.Equal("INVALID_ARGUMENT", invalid.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        }

        var inputFile = WriteInputFile(project, "{\"relativePath\":\"a.txt\"}");
        var (mixedExit, mixedStdout, _) = await RunAsync(
            "run", "workspace.read",
            "--input", inputFile,
            "--input-json", "{\"relativePath\":\"a.txt\"}",
            "--project", project.ProjectPath);
        Assert.Equal(2, mixedExit);
        using var mixed = JsonDocument.Parse(mixedStdout);
        Assert.Equal("INVALID_ARGUMENT", mixed.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task DuplicateOptionAndOptionAsValue_ReturnInvalidArgument()
    {
        using var project = new TemporaryProjectDirectory();

        var (duplicateExit, duplicateStdout, _) = await RunAsync(
            "doctor", "--project", project.ProjectPath, "--project", project.ProjectPath);
        Assert.Equal(2, duplicateExit);
        using (var duplicate = JsonDocument.Parse(duplicateStdout))
        {
            Assert.Contains("重复", duplicate.RootElement.GetProperty("errors")[0].GetProperty("message").GetString(),
                StringComparison.Ordinal);
        }

        var (valueExit, valueStdout, _) = await RunAsync("doctor", "--project", "--json");
        Assert.Equal(2, valueExit);
        using var value = JsonDocument.Parse(valueStdout);
        Assert.Equal("INVALID_ARGUMENT", value.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task ActivityList_Json_ReturnsRecordedOperations()
    {
        using var project = new TemporaryProjectDirectory();
        await RunAsync("doctor", "--project", project.ProjectPath);
        await RunAsync("operations", "list", "--project", project.ProjectPath);

        var (exitCode, stdout, stderr) = await RunAsync(
            "activity", "list", "--project", project.ProjectPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(2, document.RootElement.GetProperty("count").GetInt32());
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(items, item => item.GetProperty("operationId").GetString() == "system.doctor");
        Assert.Contains(items, item => item.GetProperty("operationId").GetString() == "operations.list");
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
        await RunAsync("doctor", "--project", project.ProjectPath);
        var activityId = ReadActivityId(project);

        var (exitCode, stdout, stderr) = await RunAsync(
            "activity", "show", activityId, "--project", project.ProjectPath);

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
        await RunAsync("doctor", "--project", project.ProjectPath);
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
            "activity", "show", "doesnotexist", "--project", project.ProjectPath);

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
    public async Task NoArguments_WritesUsageToStderrAndJsonErrorToStdout()
    {
        var (exitCode, stdout, stderr) = await RunAsync();

        Assert.Equal(2, exitCode);
        Assert.Contains("用法", stderr, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("INVALID_ARGUMENT", document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    private static string WriteInputFile(TemporaryProjectDirectory project, string json)
    {
        // 输入文件放在 .bdtool 下，避免被 workspace.search 当成项目内容命中。
        var directory = Path.Combine(project.ProjectPath, ".bdtool", "test-inputs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("n") + ".json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private static void WriteProjectVersion(TemporaryProjectDirectory project, string version)
    {
        var directory = Path.Combine(project.ProjectPath, "ProjectSettings");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "ProjectVersion.txt"),
            "m_EditorVersion: " + version + "\n",
            new UTF8Encoding(false));
    }

    private static void WriteText(TemporaryProjectDirectory project, string relativePath, string content)
    {
        WriteBytes(project, relativePath, Encoding.UTF8.GetBytes(content));
    }

    private static void WriteBytes(TemporaryProjectDirectory project, string relativePath, byte[] bytes)
    {
        var path = Path.Combine(project.ProjectPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static string Sha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
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

    /// <summary>
    /// 断言非 typed 失败信封中的 data 是存在的空对象。
    /// 统一使用 {} 而不是 null，可让调用方始终按对象处理 data。
    /// </summary>
    private static void AssertEmptyObjectData(JsonElement root)
    {
        Assert.True(root.TryGetProperty("data", out var data), "失败信封必须包含 data 属性。");
        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.Empty(data.EnumerateObject());
    }
}
