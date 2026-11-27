using BDTool.Presentation.States;

namespace BDTool.Presentation.ViewModels;

/// <summary>M8.1 Shell 占位页。后续阶段以真实 ViewModel 替换，模块注册不变。</summary>
public sealed class PlaceholderPageViewModel : PageViewModelBase
{
    public PlaceholderPageViewModel(string id, string title, string subtitle)
        : base(id, title, subtitle)
    {
    }

    public string CapabilityNote { get; init; } =
        "页面契约已注册；数据接入将在后续 M8 子阶段完成。";

    public static PlaceholderPageViewModel ComingSoon(
        string id,
        string title,
        string subtitle) =>
        new(id, title, subtitle)
        {
            StateTitle = "等待数据接入",
            StateMessage = subtitle,
            State = PageViewState.Empty
        };
}
