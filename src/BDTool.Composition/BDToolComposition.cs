using System;
using System.IO;
using BDTool.Application;
using BDTool.Application.Diagnostics;
using BDTool.Application.Offline;
using BDTool.Activity;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;
using BDTool.Operations.Catalog;
using BDTool.Operations.Diagnostics;
using BDTool.Operations.Handlers;
using BDTool.Operations.Operations;
using BDTool.Operations.Projects;
using BDTool.Operations.Workspace;
using BDTool.Provider.Local;
using BDTool.Provider.Akio;

namespace BDTool.Composition;

/// <summary>
/// 唯一组合根。使用显式工厂而不是 DI 容器：依赖顺序和空实现都在这里可见，
/// CLI 不得自行构造目录、注册表或网关。
/// </summary>
public static class BDToolComposition
{
    /// <summary>输出目录下的默认目录文件相对路径。</summary>
    public const string DefaultCatalogRelativePath = "catalog/operations.json";

    /// <summary>
    /// 装配运行时：目录 + Workspace/可选 Editor Provider + Handler + 文件存储 + 网关。
    /// 基础设施只在这里创建，CLI 通过运行时访问器查询 Activity。
    /// </summary>
    public static BDToolRuntime Create(BDToolCompositionOptions? options = null)
    {
        options ??= new BDToolCompositionOptions();
        var projectPath = string.IsNullOrWhiteSpace(options.ProjectPath)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(options.ProjectPath);
        var catalogPath = ResolveCatalogPath(options.CatalogPath);

        var catalog = OperationCatalogLoader.Load(catalogPath);

        // 项目级存储负责原子写入与查询，网关只接触端口。
        var activityStore = new FileActivityStore(projectPath);
        var artifactStore = new FileArtifactStore(projectPath);

        // 唯一允许直接依赖 Provider.Local 的位置。Provider 绑定项目根，
        // 描述快照注入 Application/Doctor/能力检查，业务层不按品牌分支。
        var workspace = new LocalWorkspaceKernel(projectPath);
        var workspaceProvider = workspace.DescribeAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var providers = new List<ProviderDescriptor> { workspaceProvider };

        IEditorKernel? editor = null;
        var unityCliPath = ResolveUnityCliPath(options.UnityCliPath);
        if (!string.IsNullOrWhiteSpace(unityCliPath))
        {
            editor = new AkioEditorKernel(
                new AkioEditorOptions
                {
                    UnityCliPath = unityCliPath,
                    ProjectPath = projectPath,
                    UnityEditorPath = ResolveUnityEditorPath(options.UnityEditorPath) ?? string.Empty,
                    DefaultTimeoutMilliseconds = options.EditorTimeoutMilliseconds
                });
            providers.Add(editor.DescribeAsync(CancellationToken.None).GetAwaiter().GetResult());
        }

        // 一致性校验保证目录不会谎报可用，也不会遗漏已注册 Handler。
        var handlers = new OperationHandlerRegistry();
        handlers.Register(new DoctorHandler(
            new FileSystemDoctorEnvironmentProbe(),
            catalog,
            options.AppVersion,
            projectPath,
            providers,
            editor));
        handlers.Register(new OperationsListHandler(catalog));
        handlers.Register(new OperationsDescribeHandler(catalog));
        handlers.Register(new ProjectContextHandler());
        handlers.Register(new ProjectInspectHandler());
        handlers.Register(new ProjectProbeHandler());
        handlers.Register(new ProjectAssetSnapshotHandler());
        handlers.Register(new PrefabSetActiveHandler());
        handlers.Register(new WorkspaceReadHandler());
        handlers.Register(new WorkspaceSearchHandler());
        handlers.Register(new WorkspaceFingerprintHandler());
        handlers.Register(new WorkspaceApplyHandler());
        handlers.EnsureConsistent(catalog);

        var capabilities = new CatalogCapabilityInspector(catalog, handlers, providers);
        var contextFactory = new OperationContextFactory(
            workspace,
            artifactStore,
            capabilities,
            editor);
        var gateway = new ApplicationGateway(
            catalog,
            handlers,
            capabilities,
            contextFactory,
            activityStore,
            projectPath: projectPath,
            appVersion: options.AppVersion,
            providers: providers,
            artifacts: artifactStore,
            executionProviderId: workspaceProvider.Id);

        return new BDToolRuntime(catalog, handlers, gateway, activityStore, projectPath, catalogPath);
    }

    /// <summary>解析目录文件路径：显式路径优先，否则取应用输出目录。</summary>
    public static string ResolveCatalogPath(string? catalogPath)
    {
        if (!string.IsNullOrWhiteSpace(catalogPath))
        {
            return Path.GetFullPath(catalogPath);
        }

        var basePath = Path.Combine(AppContext.BaseDirectory, "catalog", "operations.json");
        return Path.GetFullPath(basePath);
    }

    /// <summary>
    /// 按显式路径、BDTOOL_UNITY_CLI_PATH、当前目录及祖先目录下 .tools、PATH 的顺序
    /// 寻找 `unity-cli.exe`。CLI 常从仓库根启动，仓库根的 .tools/unity-cli 是默认安装
    /// 位置。显式/环境变量给出但文件不存在时不静默回退到别的版本；PATH 只读不改。
    /// 只返回真实存在的文件，缺失时 Editor Provider 不参与装配。
    /// </summary>
    public static string? ResolveUnityCliPath(string? configuredPath = null)
    {
        var executableName = OperatingSystem.IsWindows() ? "unity-cli.exe" : "unity-cli";

        // 显式参数优先级最高；调用方指定了 A 却静默连到 PATH 上的 B 会让诊断失真。
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        }

        var environmentPath = Environment.GetEnvironmentVariable("BDTOOL_UNITY_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return File.Exists(environmentPath) ? Path.GetFullPath(environmentPath) : null;
        }

        // 从当前目录逐级向上找仓库内默认安装位置，覆盖“从仓库根或任意子目录启动”。
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
             directory is not null;
             directory = directory.Parent)
        {
            var ridCandidate = Path.Combine(
                directory.FullName,
                ".tools",
                "unity-cli",
                "win-x64",
                executableName);
            if (File.Exists(ridCandidate))
            {
                return Path.GetFullPath(ridCandidate);
            }

            var flatCandidate = Path.Combine(
                directory.FullName,
                ".tools",
                "unity-cli",
                executableName);
            if (File.Exists(flatCandidate))
            {
                return Path.GetFullPath(flatCandidate);
            }
        }

        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in pathEntries)
        {
            var candidate = Path.Combine(entry.Trim(), executableName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>解析可选 Unity Editor 路径，不校验版本，由版本闸门负责。</summary>
    public static string? ResolveUnityEditorPath(string? configuredPath = null)
    {
        var candidate = !string.IsNullOrWhiteSpace(configuredPath)
            ? configuredPath
            : Environment.GetEnvironmentVariable("BDTOOL_UNITY_EDITOR_PATH");
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = Environment.GetEnvironmentVariable("UNITY_EDITOR_PATH");
        }

        return !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)
            ? Path.GetFullPath(candidate)
            : null;
    }
}
