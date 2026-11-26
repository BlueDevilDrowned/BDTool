namespace BDTool.Provider.Akio;

/// <summary>
/// `unity-cli` 子进程边界。生产实现负责参数数组、stdout/stderr 分离、
/// 超时与取消；测试可以在不启动 Unity 的情况下验证协议映射。
/// </summary>
public interface IUnityCliProcessRunner
{
    Task<UnityCliProcessResult> RunAsync(
        UnityCliInvocation invocation,
        CancellationToken cancellationToken);
}

/// <summary>一次 CLI 调用。参数始终以数组传递，禁止拼接到 shell 命令。</summary>
public sealed class UnityCliInvocation
{
    public string ExecutablePath { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public List<string> Arguments { get; set; } = new List<string>();
    public int TimeoutMilliseconds { get; set; } = 60_000;
    public string? UnityEditorPath { get; set; }
}

/// <summary>子进程的真实退出结果；stderr 只用于诊断，不能作为 JSON 协议。</summary>
public sealed class UnityCliProcessResult
{
    public int ExitCode { get; set; }
    public string Stdout { get; set; } = string.Empty;
    public string Stderr { get; set; } = string.Empty;
}
