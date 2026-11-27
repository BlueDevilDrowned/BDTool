using System.IO;
using System.Windows;
using BDTool.Composition;
using BDTool.Contracts.Capabilities;
using BDTool.Desktop.VisualCapture;
using BDTool.Presentation.Modules;
using BDTool.Presentation.Navigation;
using BDTool.Presentation.ViewModels;

namespace BDTool.Desktop;

/// <summary>Desktop 组合入口。WPF 只绑定 Presentation，不直接访问 Provider。</summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var projectPath = ResolveProjectPath(e.Args);
        ShellViewModel shell;
        try
        {
            var runtime = BDToolComposition.Create(new BDToolCompositionOptions
            {
                ProjectPath = projectPath
            });
            var capabilities = runtime.Capabilities
                .GetAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            shell = CreateShell(capabilities, runtime.ProjectPath, string.Empty);
        }
        catch (Exception exception)
        {
            shell = CreateShell(
                new CapabilitySnapshot(),
                Path.GetFullPath(projectPath),
                exception.Message);
        }

        var window = new MainWindow
        {
            DataContext = shell
        };
        MainWindow = window;
        var capturePath = Environment.GetEnvironmentVariable("BDTOOL_CAPTURE_PATH");
        if (!string.IsNullOrWhiteSpace(capturePath))
        {
            WindowScreenshotRenderer.Capture(
                window,
                capturePath,
                width: 1440,
                height: 900);
            Shutdown();
            return;
        }

        window.Show();
    }

    private static ShellViewModel CreateShell(
        CapabilitySnapshot capabilities,
        string projectPath,
        string error)
    {
        var unity = capabilities.Capabilities.TryGetValue("editor.status", out var ready) && ready
            ? "Editor 已连接"
            : "Editor 未连接";
        var provider = string.IsNullOrWhiteSpace(capabilities.ProviderId)
            ? "Provider 未就绪"
            : capabilities.ProviderId;
        return new ShellViewModel(
            new ModuleRegistry(StandardModules.Create()),
            capabilities,
            projectPath,
            unity,
            provider,
            error);
    }

    private static string ResolveProjectPath(IReadOnlyList<string> args)
    {
        var configured = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("BDTOOL_PROJECT_PATH"))
            ? Environment.GetEnvironmentVariable("BDTOOL_PROJECT_PATH")
            : args.FirstOrDefault();
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Directory.GetCurrentDirectory()
            : configured);
    }
}
