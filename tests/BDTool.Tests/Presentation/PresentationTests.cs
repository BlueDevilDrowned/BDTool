using BDTool.Contracts.Capabilities;
using BDTool.Presentation.Modules;
using BDTool.Presentation.Navigation;
using BDTool.Presentation.States;
using BDTool.Presentation.ViewModels;

namespace BDTool.Tests.Presentation;

/// <summary>M8.1 Shell 的模块顺序、状态语义和页面状态测试。</summary>
public sealed class PresentationTests
{
    [Fact]
    public void StandardModules_AreOrderedAndComplete()
    {
        var navigation = new ModuleRegistry(StandardModules.Create())
            .CreateNavigation(new CapabilitySnapshot());

        Assert.Equal(
            new[]
            {
                "overview", "unity", "codex", "operations",
                "activity", "artifacts", "formation", "settings"
            },
            navigation.Select(item => item.Id));
    }

    [Fact]
    public void Shell_SelectsOverviewByDefault()
    {
        var shell = new ShellViewModel(
            new ModuleRegistry(StandardModules.Create()),
            new CapabilitySnapshot(),
            @"E:\project",
            "Editor 未连接",
            "local");

        Assert.Equal("overview", shell.SelectedNavigation?.Id);
        Assert.Equal("Overview", shell.CurrentPage?.Title);
        Assert.Equal(8, shell.NavigationItems.Count);
    }

    [Theory]
    [InlineData("Ready", StatusSemantic.Success)]
    [InlineData("Passed", StatusSemantic.Success)]
    [InlineData("Running", StatusSemantic.Running)]
    [InlineData("NeedsRework", StatusSemantic.Warning)]
    [InlineData("Blocked", StatusSemantic.Failed)]
    [InlineData("DeliveryUnknown", StatusSemantic.Uncertain)]
    [InlineData("Unsupported", StatusSemantic.Unsupported)]
    [InlineData("Cancelled", StatusSemantic.Cancelled)]
    public void StatusText_UsesSingleSemanticMapping(
        string text,
        StatusSemantic expected)
    {
        Assert.Equal(expected, StatusSemantics.FromText(text));
    }

    [Fact]
    public void ModuleRegistry_RejectsDuplicateIds()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ModuleRegistry(new IUiModule[]
            {
                new TestModule("same", 1),
                new TestModule("same", 2)
            }));

        Assert.Contains("same", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaceholderPage_UsesCanonicalEmptyState()
    {
        var page = PlaceholderPageViewModel.ComingSoon("unity", "Unity", "等待 Editor");

        Assert.Equal(PageViewState.Empty, page.State);
        Assert.True(page.IsEmpty);
        Assert.Equal("等待数据接入", page.StateTitle);
    }

    private sealed class TestModule : IUiModule
    {
        public TestModule(string id, int order)
        {
            Id = id;
            Order = order;
        }

        public string Id { get; }

        public string Title => Id;

        public int Order { get; }

        public bool IsAvailable(CapabilitySnapshot capabilities) => true;

        public IPageViewModel CreateViewModel() =>
            new PlaceholderPageViewModel(Id, Title, Title);
    }
}
