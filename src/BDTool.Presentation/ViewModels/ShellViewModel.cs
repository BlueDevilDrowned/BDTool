using BDTool.Contracts.Capabilities;
using BDTool.Presentation.Commands;
using BDTool.Presentation.Navigation;

namespace BDTool.Presentation.ViewModels;

/// <summary>
/// 主壳状态：模块、当前页、全局项目摘要和命令面板骨架。壳不包含页面业务分支。
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private NavigationItemViewModel? _selectedNavigation;
    private IPageViewModel? _currentPage;
    private bool _isCommandPaletteOpen;
    private string _commandSearch = string.Empty;
    private string _projectPath;
    private string _unityStatus;
    private string _providerStatus;
    private string _globalError;

    public ShellViewModel(
        ModuleRegistry registry,
        CapabilitySnapshot capabilities,
        string projectPath,
        string unityStatus,
        string providerStatus,
        string globalError = "")
    {
        NavigationItems = registry.CreateNavigation(capabilities);
        _projectPath = projectPath;
        _unityStatus = unityStatus;
        _providerStatus = providerStatus;
        _globalError = globalError;
        ToggleCommandPaletteCommand = new RelayCommand(
            () => IsCommandPaletteOpen = !IsCommandPaletteOpen);
        CloseCommandPaletteCommand = new RelayCommand(() => IsCommandPaletteOpen = false);
        RefreshCommand = new RelayCommand(() => { });
        if (NavigationItems.Count > 0)
        {
            SelectedNavigation = NavigationItems[0];
        }
    }

    public IReadOnlyList<NavigationItemViewModel> NavigationItems { get; }

    public NavigationItemViewModel? SelectedNavigation
    {
        get => _selectedNavigation;
        set
        {
            if (SetProperty(ref _selectedNavigation, value))
            {
                CurrentPage = value?.Page;
            }
        }
    }

    public IPageViewModel? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public bool IsCommandPaletteOpen
    {
        get => _isCommandPaletteOpen;
        set => SetProperty(ref _isCommandPaletteOpen, value);
    }

    public string CommandSearch
    {
        get => _commandSearch;
        set => SetProperty(ref _commandSearch, value);
    }

    public string ProjectPath
    {
        get => _projectPath;
        set => SetProperty(ref _projectPath, value);
    }

    public string UnityStatus
    {
        get => _unityStatus;
        set => SetProperty(ref _unityStatus, value);
    }

    public string ProviderStatus
    {
        get => _providerStatus;
        set => SetProperty(ref _providerStatus, value);
    }

    public string GlobalError
    {
        get => _globalError;
        set
        {
            if (SetProperty(ref _globalError, value))
            {
                OnPropertyChanged(nameof(HasGlobalError));
            }
        }
    }

    public bool HasGlobalError => !string.IsNullOrWhiteSpace(GlobalError);

    public RelayCommand ToggleCommandPaletteCommand { get; }

    public RelayCommand CloseCommandPaletteCommand { get; }

    public RelayCommand RefreshCommand { get; }
}
