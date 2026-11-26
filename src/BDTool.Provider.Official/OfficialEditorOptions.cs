namespace BDTool.Provider.Official;

/// <summary>
/// 官方 Pipeline Provider 配置。Provider 通过项目内公开的实例描述文件发现
/// 正在运行的 Editor，不依赖 Unity Hub CLI，也不修改官方包。
/// </summary>
public sealed class OfficialEditorOptions
{
    public string ProjectPath { get; set; } = string.Empty;
    public int DefaultTimeoutMilliseconds { get; set; } = 60_000;
    public int CompletedJobRetentionMilliseconds { get; set; } = 30_000;
    public int CancelWaitMilliseconds { get; set; } = 5_000;
}
