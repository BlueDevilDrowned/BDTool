using BDTool.Contracts.Capabilities;

namespace BDTool.Presentation.Navigation;

/// <summary>桌面导航模块契约。所有页面通过模块注册加入，主壳不按页面类型写业务分支。</summary>
public interface IUiModule
{
    string Id { get; }

    string Title { get; }

    int Order { get; }

    bool IsAvailable(CapabilitySnapshot capabilities);

    IPageViewModel CreateViewModel();
}
