using System;
using System.Collections.Generic;
using BDTool.Contracts.Operations;

namespace BDTool.Operations.Catalog;

/// <summary>
/// 只读操作目录。构造时校验 ID 唯一性和描述合法性，之后不再允许修改，
/// 让应用层与 CLI 看到同一份稳定快照。
/// </summary>
public sealed class OperationCatalog : IOperationCatalog
{
    private readonly List<OperationDescriptor> _items;
    private readonly Dictionary<string, OperationDescriptor> _byId;

    /// <summary>构造并校验目录。</summary>
    public OperationCatalog(int version, IEnumerable<OperationDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        if (version < 1)
        {
            throw new OperationCatalogException("操作目录 version 必须大于等于 1。");
        }

        Version = version;
        _items = new List<OperationDescriptor>();
        _byId = new Dictionary<string, OperationDescriptor>(StringComparer.Ordinal);

        foreach (var descriptor in descriptors)
        {
            var errors = OperationDescriptorValidator.Validate(descriptor);
            if (errors.Count > 0)
            {
                throw new OperationCatalogException(
                    $"操作 {descriptor.Id} 不合法：{string.Join("；", errors)}");
            }

            if (_byId.ContainsKey(descriptor.Id))
            {
                throw new OperationCatalogException($"操作 ID 重复：{descriptor.Id}");
            }

            _items.Add(descriptor);
            _byId.Add(descriptor.Id, descriptor);
        }
    }

    /// <summary>目录文档版本。</summary>
    public int Version { get; }

    /// <inheritdoc />
    public IReadOnlyList<OperationDescriptor> GetAll() => _items;

    /// <inheritdoc />
    public OperationDescriptor? Find(string? operationId)
    {
        if (string.IsNullOrEmpty(operationId))
        {
            return null;
        }

        return _byId.TryGetValue(operationId, out var descriptor) ? descriptor : null;
    }
}
