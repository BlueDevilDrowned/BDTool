namespace BDTool.Presentation.States;

/// <summary>跨 Activity、Formation 和设置页面共享的状态语义。</summary>
public enum StatusSemantic
{
    Success,
    Running,
    Warning,
    Failed,
    Uncertain,
    Unsupported,
    Cancelled,
    Neutral
}
