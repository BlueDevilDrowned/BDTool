namespace BDTool.Presentation.Navigation;

/// <summary>页面最小契约；具体页面可增加只读数据与命令。</summary>
public interface IPageViewModel
{
    string Id { get; }

    string Title { get; }

    string Subtitle { get; }
}
