using System.ComponentModel;
using System.Diagnostics;

namespace BDTool.Provider.Akio;

/// <summary>
/// 使用进程参数数组启动 unity-cli。取消或超时会尝试终止仍可确认运行的
/// 父进程及其进程树；若父进程已先退出，则当前句柄无法确认或清理其遗留子进程。
/// </summary>
public sealed class UnityCliProcessRunner : IUnityCliProcessRunner
{
    public async Task<UnityCliProcessResult> RunAsync(
        UnityCliInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.ExecutablePath,
            WorkingDirectory = invocation.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(invocation.UnityEditorPath))
        {
            startInfo.Environment["UNITY_EDITOR_PATH"] = invocation.UnityEditorPath;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!TryStartProcess(process))
        {
            throw new EditorStartException("无法启动 unity-cli 进程。");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Clamp(invocation.TimeoutMilliseconds, 1, int.MaxValue));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw new TimeoutException(
                "unity-cli 在 " + invocation.TimeoutMilliseconds + " ms 内未退出。");
        }
        catch
        {
            Kill(process);
            throw;
        }

        return new UnityCliProcessResult
        {
            ExitCode = process.ExitCode,
            Stdout = await stdoutTask.ConfigureAwait(false),
            Stderr = await stderrTask.ConfigureAwait(false)
        };
    }

    private static void Kill(Process process)
    {
        bool isRunning;
        try
        {
            isRunning = !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (Win32Exception)
        {
            return;
        }

        if (!isRunning)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static bool TryStartProcess(Process process)
    {
        try
        {
            return process.Start();
        }
        catch (Exception exception)
        {
            throw new EditorStartException("无法启动 unity-cli 进程。", exception);
        }
    }
}

/// <summary>进程无法启动时使用的内部异常，由 Provider 统一转换。</summary>
public sealed class EditorStartException : Exception
{
    public EditorStartException(string message) : base(message)
    {
    }

    public EditorStartException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
