namespace BDTool.Presentation.States;

/// <summary>统一页面状态，不把网络、Editor 或 Provider 的原始状态直接暴露给 View。</summary>
public enum PageViewState
{
    Ready,
    Loading,
    Empty,
    Error,
    Unsupported
}
