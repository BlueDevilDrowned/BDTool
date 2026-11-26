namespace BDTool.Provider.Akio;

/// <summary>
/// Akio Editor Provider 的进程级配置。Provider 只依赖 Contracts；
/// 可执行文件、项目根和超时均由组合根显式注入。
/// </summary>
public sealed class AkioEditorOptions
{
    public string UnityCliPath { get; set; } = string.Empty;
    public string ProjectPath { get; set; } = string.Empty;
    public string UnityEditorPath { get; set; } = string.Empty;
    public int DefaultTimeoutMilliseconds { get; set; } = 60_000;

    /// <summary>
    /// 完成结果在 Job 表中最短保留时间。调用方可在此窗口内重复查询同一结果；
    /// 窗口结束后移除 Job 并释放其取消令牌源，避免长期占用内存。
    /// </summary>
    public int CompletedJobRetentionMilliseconds { get; set; } = 30_000;

    /// <summary>
    /// Cancel 发出取消后等待 Job 收敛完成的有界时间。测试可缩短该值，
    /// 生产环境保留足够时间让进程终止和错误结果发布。
    /// </summary>
    public int CancelWaitMilliseconds { get; set; } = 5_000;
}
