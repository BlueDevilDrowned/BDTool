using System;
using System.IO;
using BDTool.Application;
using BDTool.Application.Diagnostics;
using BDTool.Application.Offline;
using BDTool.Activity;
using BDTool.Contracts.Operations;
using BDTool.Operations.Catalog;
using BDTool.Operations.Diagnostics;
using BDTool.Operations.Handlers;
using BDTool.Operations.Operations;
using BDTool.Operations.Projects;
using BDTool.Operations.Workspace;
using BDTool.Provider.Local;

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
    /// 装配 M1 运行时：目录 + 本地 Workspace Provider + 八个 Handler + 文件存储 + 网关。
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
        var provider = workspace.DescribeAsync(CancellationToken.None).GetAwaiter().GetResult();
        var providers = new[] { provider };

        // 一致性校验保证目录不会谎报可用，也不会遗漏已注册 Handler。
        var handlers = new OperationHandlerRegistry();
        handlers.Register(new DoctorHandler(
            new FileSystemDoctorEnvironmentProbe(),
            catalog,
            options.AppVersion,
            projectPath,
            providers));
        handlers.Register(new OperationsListHandler(catalog));
        handlers.Register(new OperationsDescribeHandler(catalog));
        handlers.Register(new ProjectContextHandler());
        handlers.Register(new WorkspaceReadHandler());
        handlers.Register(new WorkspaceSearchHandler());
        handlers.Register(new WorkspaceFingerprintHandler());
        handlers.Register(new WorkspaceApplyHandler());
        handlers.EnsureConsistent(catalog);

        var capabilities = new CatalogCapabilityInspector(catalog, handlers, providers);
        var contextFactory = new OperationContextFactory(workspace, artifactStore, capabilities);
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
            executionProviderId: provider.Id);

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
}
