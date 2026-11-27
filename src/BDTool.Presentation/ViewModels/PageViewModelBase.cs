using BDTool.Presentation.Navigation;
using BDTool.Presentation.States;

namespace BDTool.Presentation.ViewModels;

/// <summary>页面公共状态：加载、空、错误和不支持不能由各页面重新定义。</summary>
public abstract class PageViewModelBase : ObservableObject, IPageViewModel
{
    private PageViewState _state = PageViewState.Ready;
    private string _stateTitle = string.Empty;
    private string _stateMessage = string.Empty;

    protected PageViewModelBase(string id, string title, string subtitle)
    {
        Id = id;
        Title = title;
        Subtitle = subtitle;
    }

    public string Id { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public PageViewState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(IsUnsupported));
            }
        }
    }

    public string StateTitle
    {
        get => _stateTitle;
        protected set => SetProperty(ref _stateTitle, value);
    }

    public string StateMessage
    {
        get => _stateMessage;
        protected set => SetProperty(ref _stateMessage, value);
    }

    public bool IsLoading => State == PageViewState.Loading;

    public bool IsEmpty => State == PageViewState.Empty;

    public bool HasError => State == PageViewState.Error;

    public bool IsUnsupported => State == PageViewState.Unsupported;

    protected void SetEmpty(string title, string message)
    {
        StateTitle = title;
        StateMessage = message;
        State = PageViewState.Empty;
    }

    protected void SetError(string title, string message)
    {
        StateTitle = title;
        StateMessage = message;
        State = PageViewState.Error;
    }

    protected void SetUnsupported(string title, string message)
    {
        StateTitle = title;
        StateMessage = message;
        State = PageViewState.Unsupported;
    }
}
