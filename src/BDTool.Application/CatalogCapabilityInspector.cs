using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts;
using BDTool.Contracts.Capabilities;
using BDTool.Contracts.Operations;
using BDTool.Core.Activity;
using BDTool.Operations.Handlers;

namespace BDTool.Application;

/// <summary>
/// 只根据操作目录与 Handler 注册表报告能力，不探测 Provider 或 Unity。
/// 未注册 Handler 的操作一律报告为 false。
/// </summary>
public sealed class CatalogCapabilityInspector : ICapabilityInspector
{
    private readonly IOperationCatalog _catalog;
    private readonly IOperationHandlerRegistry _handlers;

    public CatalogCapabilityInspector(IOperationCatalog catalog, IOperationHandlerRegistry handlers)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    }

    /// <inheritdoc />
    public Task<CapabilitySnapshot> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var capabilities = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var descriptor in _catalog.GetAll())
        {
            capabilities[descriptor.Id] = _handlers.Find(descriptor.Id) is not null;
        }

        var snapshot = new CapabilitySnapshot
        {
            ProviderId = string.Empty,
            ContractVersion = BDToolContract.Version,
            Capabilities = capabilities,
            Limits = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["activity.largeContentThresholdBytes"] =
                    ActivityPolicy.LargeContentThresholdBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["activity.retentionDays"] =
                    ActivityPolicy.DefaultRetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };

        return Task.FromResult(snapshot);
    }
}
