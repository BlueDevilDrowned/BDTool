using System;
using System.Collections.Generic;
using BDTool.Contracts.Operations;
using BDTool.Core.Operations;

namespace BDTool.Operations.Handlers;

/// <summary>
/// Handler 查询端口。Application 只读取注册表，不自行创建 Handler。
/// </summary>
public interface IOperationHandlerRegistry
{
    IReadOnlyList<IOperationHandler> GetAll();
    IOperationHandler? Find(string? operationId);
}

/// <summary>
/// 组合期可写的 Handler 注册表。只登记当前构建真实可执行的 Handler；
/// 目录中的 implemented 与 availability 必须与注册结果一致，一致性校验由组合根在启动时强制执行。
/// </summary>
public sealed class OperationHandlerRegistry : IOperationHandlerRegistry
{
    private readonly List<IOperationHandler> _handlers = new List<IOperationHandler>();
    private readonly Dictionary<string, IOperationHandler> _byId =
        new Dictionary<string, IOperationHandler>(StringComparer.Ordinal);

    /// <summary>已注册 Handler 数量。</summary>
    public int Count => _handlers.Count;

    /// <summary>注册 Handler；ID 非法、重复或与目录冲突时立即失败。</summary>
    public OperationHandlerRegistry Register(IOperationHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!OperationIdRules.IsValid(handler.OperationId, out var error))
        {
            throw new ArgumentException($"Handler 的 OperationId 非法：{error}", nameof(handler));
        }

        if (_byId.ContainsKey(handler.OperationId))
        {
            throw new InvalidOperationException($"Handler 重复注册：{handler.OperationId}");
        }

        _handlers.Add(handler);
        _byId.Add(handler.OperationId, handler);
        return this;
    }

    /// <summary>
    /// 校验目录与注册表一致：目录声称实现项必须有 Handler，未实现项不得有 Handler，
    /// 只有注册了 Handler 的操作才允许 availability=available。
    /// </summary>
    public void EnsureConsistent(IOperationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        foreach (var handler in _handlers)
        {
            if (catalog.Find(handler.OperationId) is null)
            {
                throw new InvalidOperationException($"Handler 未在操作目录登记：{handler.OperationId}");
            }
        }

        foreach (var descriptor in catalog.GetAll())
        {
            var registered = Find(descriptor.Id) is not null;
            if (descriptor.Implemented != registered)
            {
                throw new InvalidOperationException(
                    $"操作 {descriptor.Id} 的 implemented={descriptor.Implemented} 与 Handler 注册状态不一致。");
            }

            var claimedAvailable = string.Equals(
                descriptor.Availability,
                OperationAvailability.Available,
                StringComparison.Ordinal);
            if (claimedAvailable != registered)
            {
                throw new InvalidOperationException(
                    $"操作 {descriptor.Id} 的 availability={descriptor.Availability} 与 Handler 注册状态不一致。");
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IOperationHandler> GetAll() => _handlers;

    /// <inheritdoc />
    public IOperationHandler? Find(string? operationId)
    {
        if (string.IsNullOrEmpty(operationId))
        {
            return null;
        }

        return _byId.TryGetValue(operationId, out var handler) ? handler : null;
    }
}
