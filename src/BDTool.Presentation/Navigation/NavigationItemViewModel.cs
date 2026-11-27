namespace BDTool.Presentation.Navigation;

/// <summary>左侧导航项。模块是唯一来源，选中项只影响当前页面。</summary>
public sealed class NavigationItemViewModel
{
    public NavigationItemViewModel(IUiModule module, IPageViewModel page)
    {
        Module = module;
        Page = page;
    }

    public IUiModule Module { get; }

    public IPageViewModel Page { get; }

    public string Id => Module.Id;

    public string Title => Module.Title;
}
