using BDTool.Provider.Akio;

namespace BDTool.Tests.Editor;

public sealed class UnityCliProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_SeparatesStdoutStderrAndPreservesExitCode()
    {
        var runner = new UnityCliProcessRunner();
        var result = await runner.RunAsync(
            new UnityCliInvocation
            {
                ExecutablePath = "cmd.exe",
                WorkingDirectory = AppContext.BaseDirectory,
                Arguments = new List<string>
                {
                    "/d",
                    "/c",
                    "echo machine-json & echo diagnostic 1>&2 & exit /b 5"
                },
                TimeoutMilliseconds = 5_000
            },
            CancellationToken.None);

        Assert.Equal(5, result.ExitCode);
        Assert.Contains("machine-json", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("diagnostic", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_KillsProcessWhenTimeoutElapsed()
    {
        var runner = new UnityCliProcessRunner();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            runner.RunAsync(
                new UnityCliInvocation
                {
                    ExecutablePath = "cmd.exe",
                    WorkingDirectory = AppContext.BaseDirectory,
                    Arguments = new List<string>
                    {
                        "/d",
                        "/c",
                        "ping 127.0.0.1 -n 6 > nul"
                    },
                    TimeoutMilliseconds = 100
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_WrapsProcessStartFailure()
    {
        var runner = new UnityCliProcessRunner();

        var exception = await Assert.ThrowsAsync<EditorStartException>(() =>
            runner.RunAsync(
                new UnityCliInvocation
                {
                    ExecutablePath = @"C:\missing-tools\unity-cli-missing.exe",
                    WorkingDirectory = AppContext.BaseDirectory,
                    TimeoutMilliseconds = 1_000
                },
                CancellationToken.None));

        Assert.IsType<System.ComponentModel.Win32Exception>(exception.InnerException);
    }
}
