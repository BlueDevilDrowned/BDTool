using BDTool.Contracts.Capabilities;
using BDTool.Presentation.Navigation;
using BDTool.Presentation.ViewModels;

namespace BDTool.Presentation.Modules;

/// <summary>M8 首批八个模块。占位页在后续阶段替换时不需要修改 Shell。</summary>
public static class StandardModules
{
    public static IReadOnlyList<IUiModule> Create() => new IUiModule[]
    {
        new StandardModule("overview", "Overview", 10, "项目与环境概览"),
        new StandardModule("unity", "Unity", 20, "Editor 状态、诊断和操作验收"),
        new StandardModule("codex", "Codex", 30, "Skill、Agent Profile、MCP 和配置来源"),
        new StandardModule("operations", "Operations", 40, "操作目录、Schema 和安全执行"),
        new StandardModule("activity", "Activity", 50, "CLI、MCP 与 Desktop 调用审计"),
        new StandardModule("artifacts", "Artifacts", 60, "截图、日志、测试和性能产物"),
        new StandardModule("formation", "Formation", 70, "席位、任务、审核和返工"),
        new StandardModule("settings", "Settings", 80, "项目与桌面设置")
    };

    private sealed class StandardModule : IUiModule
    {
        private readonly string _subtitle;

        public StandardModule(string id, string title, int order, string subtitle)
        {
            Id = id;
            Title = title;
            Order = order;
            _subtitle = subtitle;
        }

        public string Id { get; }

        public string Title { get; }

        public int Order { get; }

        public bool IsAvailable(CapabilitySnapshot capabilities) => true;

        public IPageViewModel CreateViewModel() =>
            PlaceholderPageViewModel.ComingSoon(Id, Title, _subtitle);
    }
}
