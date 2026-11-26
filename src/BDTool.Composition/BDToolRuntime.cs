using BDTool.Contracts.Gateway;
using BDTool.Contracts.Activity;
using BDTool.Contracts.Operations;
using BDTool.Operations.Handlers;

namespace BDTool.Composition;

/// <summary>
/// 组合完成后的应用实例。只暴露应用侧端口，不暴露任何基础设施实现，
/// 保证 CLI 与后续 UI 通过同一入口访问应用。
/// </summary>
public sealed class BDToolRuntime
{
    public BDToolRuntime(
        IOperationCatalog catalog,
        IOperationHandlerRegistry handlers,
        IApplicationGateway gateway,
        IActivityQuery activityQuery,
        string projectPath,
        string catalogPath)
    {
        Catalog = catalog;
        Handlers = handlers;
        Gateway = gateway;
        ActivityQuery = activityQuery;
        ProjectPath = projectPath;
        CatalogPath = catalogPath;
    }

    /// <summary>冻结的操作目录快照。</summary>
    public IOperationCatalog Catalog { get; }

    /// <summary>当前构建注册的 M1 Handler 集合。</summary>
    public IOperationHandlerRegistry Handlers { get; }

    /// <summary>表现层唯一执行入口。</summary>
    public IApplicationGateway Gateway { get; }

    /// <summary>
    /// Activity 查询端口。CLI 只通过该端口列出或读取活动记录，
    /// 不直接构造文件存储，也不感知 Provider 品牌。
    /// </summary>
    public IActivityQuery ActivityQuery { get; }

    /// <summary>解析后的项目路径。</summary>
    public string ProjectPath { get; }

    /// <summary>实际使用的操作目录路径，用于诊断。</summary>
    public string CatalogPath { get; }
}
