using BDTool.Contracts.Capabilities;

namespace BDTool.Presentation.Navigation;

/// <summary>按能力和 Order 构建导航快照，保证页面注册顺序稳定。</summary>
public sealed class ModuleRegistry
{
    private readonly IReadOnlyList<IUiModule> _modules;

    public ModuleRegistry(IEnumerable<IUiModule> modules)
    {
        _modules = modules
            .OrderBy(module => module.Order)
            .ThenBy(module => module.Id, StringComparer.Ordinal)
            .ToArray();
        var duplicate = _modules.GroupBy(module => module.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException("重复的 UI 模块 ID：" + duplicate.Key, nameof(modules));
        }
    }

    public IReadOnlyList<NavigationItemViewModel> CreateNavigation(
        CapabilitySnapshot capabilities) =>
        _modules.Where(module => module.IsAvailable(capabilities))
            .Select(module => new NavigationItemViewModel(module, module.CreateViewModel()))
            .ToArray();
}
