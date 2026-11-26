using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Tests.Support;

namespace BDTool.Tests.Cli;

/// <summary>
/// 用真实子进程验证 CLI 的进程级契约：stdout 只含单个 JSON 信封、stderr 不污染 stdout、
/// 退出码与 Gateway 结果一致。每个用例使用独立临时项目，不读取或写入用户项目；
/// 进程统一设置超时并在超时后杀掉整棵进程树，避免测试悬挂。
/// 说明：Ctrl+C 的真实信号投递无法跨平台稳定复现，这里不做伪测试，留作未验证风险。
/// </summary>
public sealed class CliProcessTests
{
    /// <summary>单个 CLI 子进程的最长等待时间；正常 doctor 应在秒级完成。</summary>
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task UnknownOperation_WritesSingleJsonEnvelopeAndExitCodeTwo()
    {
        using var project = new TemporaryProjectDirectory();

        var result = await RunBdtoolAsync(
            project.ProjectPath,
            "run", "no.such.operation", "--project", project.ProjectPath);

        Assert.Equal(2, result.ExitCode);
        using var document = ParseSingleJsonDocument(result.Stdout);
        var root = document.RootElement;
        // 未知操作由 Gateway 的目录查找返回 NOT_FOUND，退出码仍映射为 2（参数/未找到）。
        Assert.Equal("NOT_FOUND", root.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("data").ValueKind);

        // stdout 是唯一结果通道；stderr 即使有内容也不能混入结果信封。
        Assert.DoesNotContain("bdtool:", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"status\"", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidOption_WritesHumanDiagnosticToStderrOnlyAndExitCodeTwo()
    {
        using var project = new TemporaryProjectDirectory();

        var result = await RunBdtoolAsync(
            project.ProjectPath,
            "doctor", "--project", project.ProjectPath, "--bad-option");

        Assert.Equal(2, result.ExitCode);
        using var document = ParseSingleJsonDocument(result.Stdout);
        var root = document.RootElement;
        Assert.Equal("INVALID_ARGUMENT", root.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("data").ValueKind);

        Assert.StartsWith("bdtool: ", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("bdtool:", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_RealProjectSucceedsAndReportsUnitySkipped()
    {
        using var project = new TemporaryProjectDirectory();

        var result = await RunBdtoolAsync(
            project.ProjectPath,
            "doctor", "--project", project.ProjectPath);

        Assert.True(
            result.ExitCode == 0,
            $"exit={result.ExitCode}\nstdout={result.Stdout}\nstderr={result.Stderr}");
        Assert.Equal(string.Empty, result.Stderr);

        using var document = ParseSingleJsonDocument(result.Stdout);
        var root = document.RootElement;
        Assert.Equal("completed", root.GetProperty("status").GetString());

        var data = root.GetProperty("data");
        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.Equal("ready", data.GetProperty("state").GetString());
        Assert.Equal("M5", data.GetProperty("milestone").GetString());
        Assert.Equal("0.5.0", data.GetProperty("appVersion").GetString());

        // Unity 未接入，进程级报告也必须保持 connected=false / skipped。
        var unity = data.GetProperty("unity");
        Assert.False(unity.GetProperty("connected").GetBoolean());
        Assert.Equal("not-assembled", unity.GetProperty("integration").GetString());
        var unityCheck = Assert.Single(
            data.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("id").GetString() == "unity.connection");
        Assert.Equal("skipped", unityCheck.GetProperty("status").GetString());
    }

    [Fact]
    public async Task MissingProject_DoctorFailsWithEnvironmentNotReadyExitCode()
    {
        using var project = new TemporaryProjectDirectory();
        var missing = Path.Combine(project.ProjectPath, "missing-project");

        var result = await RunBdtoolAsync(
            project.ProjectPath,
            "doctor", "--project", missing);

        Assert.Equal(4, result.ExitCode);
        using var document = ParseSingleJsonDocument(result.Stdout);
        var root = document.RootElement;
        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal("PROJECT_NOT_FOUND", root.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task Version_PrintsProductVersionAndMilestoneM5()
    {
        using var project = new TemporaryProjectDirectory();

        var result = await RunBdtoolAsync(project.ProjectPath, "--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("bdtool 0.5.0", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("milestone M5", result.Stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Stderr);
    }

    /// <summary>
    /// 启动已构建的 bdtool.dll（测试输出目录中由项目引用复制），工作目录固定为临时项目，
    /// 防止相对路径落到用户项目。stdout/stderr 并行读取以避免管道阻塞。
    /// </summary>
    private static async Task<ProcessResult> RunBdtoolAsync(string workingDirectory, params string[] arguments)
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "bdtool.dll");
        Assert.True(
            File.Exists(dllPath),
            "未在测试输出目录找到 bdtool.dll，请先构建解决方案。路径：" + dllPath);

        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(host))
        {
            host = "dotnet";
        }

        var startInfo = new ProcessStartInfo(host)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        startInfo.ArgumentList.Add(dllPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 CLI 子进程。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(ProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            var partialStdout = await stdoutTask;
            var partialStderr = await stderrTask;
            Assert.Fail(
                $"CLI 子进程在 {ProcessTimeout.TotalSeconds:0} 秒内未退出，已杀掉进程树。" +
                $"\nstdout={partialStdout}\nstderr={partialStderr}");
        }

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    /// <summary>超时清理：只杀本次启动的进程树，避免遗留悬挂的 dotnet 子进程。</summary>
    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 进程已自行退出时无需再杀。
        }
    }

    /// <summary>解析 stdout 并要求整段只有一个 JSON 文档；尾随非空白内容会让解析失败。</summary>
    private static JsonDocument ParseSingleJsonDocument(string stdout)
    {
        Assert.False(string.IsNullOrWhiteSpace(stdout), "CLI stdout 不应为空。");
        return JsonDocument.Parse(stdout);
    }

    private readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr);
}
