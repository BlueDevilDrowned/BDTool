using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts;
using BDTool.Contracts.Capabilities;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;
using BDTool.Core.Activity;
using BDTool.Operations.Handlers;

namespace BDTool.Application;

/// <summary>
/// 合并真实 Provider 能力与操作目录/Handler 状态，不探测 Unity。
/// 两层语义保持独立：操作 ID 只记录"是否已注册 Handler"，供 UI 判断可分发性；
/// requiredCapabilities 的可用性只认 Provider 上报值——未上报、上报 false 与未知能力
/// 一律不可用，已注册 Handler 不能替代 Provider 能力。快照仍是单 Provider 扁平形状。
/// </summary>
public sealed class CatalogCapabilityInspector : ICapabilityInspector
{
    private readonly IOperationCatalog _catalog;
    private readonly IOperationHandlerRegistry _handlers;
    private readonly IReadOnlyList<ProviderDescriptor> _providers;

    public CatalogCapabilityInspector(
        IOperationCatalog catalog,
        IOperationHandlerRegistry handlers,
        IReadOnlyList<ProviderDescriptor>? providers = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
        _providers = providers ?? Array.Empty<ProviderDescriptor>();
    }

    /// <inheritdoc />
    public Task<CapabilitySnapshot> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 第一层：Provider 能力视图。只收录 Provider 真实上报的键；"未上报"在表里就是
        // 不存在，因为"不知道"不能当作"支持"。门禁稍后只认这一层。
        var providerCapabilities = new Dictionary<string, bool>(StringComparer.Ordinal);
        var limits = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in _providers)
        {
            foreach (var capability in provider.Capabilities)
            {
                // 多 Provider 上报同名能力时任一显式可用即视为可用；本轮仍是单 Provider
                // 扁平快照，能力来源归属不在本实现范围内。
                providerCapabilities[capability.Key] =
                    providerCapabilities.TryGetValue(capability.Key, out var existingReported)
                        ? existingReported || capability.Value
                        : capability.Value;
            }

            foreach (var limit in provider.Limits)
            {
                limits[limit.Key] = limit.Value;
            }
        }

        var descriptors = _catalog.GetAll();
        var capabilities = new Dictionary<string, bool>(StringComparer.Ordinal);

        // 第二层：操作注册视图。目录中每个操作 ID 记录"是否已注册 Handler"，
        // 供 UI/诊断判断该操作在当前构建里是否可分发。
        foreach (var descriptor in descriptors)
        {
            capabilities[descriptor.Id] = _handlers.Find(descriptor.Id) is not null;
        }

        // 第三层：门禁覆盖。requiredCapabilities 的可用性只能来自 Provider 上报，不能用
        // "Handler 已注册"顶替：Handler 存在只说明本进程实现了该操作，并不说明底下这层
        // Provider 真的具备对应能力。若把两者做逻辑或，Provider 明确上报 false（或干脆
        // 未上报）时仍会被注册状态抬成 true，门禁就形同虚设——workspace.read/search/
        // fingerprint 的操作 ID 与能力 ID 同名，正是这条路径。因此这里用 Provider 真值
        // 直接覆盖同名键：未上报与 false 都落成 false，未知能力同样按不可用处理；
        // 键是否同时是操作 ID 不参与判断，requiredCapabilities 本来就可能引用非操作 ID。
        foreach (var descriptor in descriptors)
        {
            foreach (var required in descriptor.RequiredCapabilities)
            {
                capabilities[required] =
                    providerCapabilities.TryGetValue(required, out var reportedEnabled) && reportedEnabled;
            }
        }

        // 第四层：其余 Provider 能力（既非操作 ID 也不在 requiredCapabilities 中，例如
        // editor.status）原样透传，保持对外扁平形状不变。
        foreach (var capability in providerCapabilities)
        {
            if (!capabilities.ContainsKey(capability.Key))
            {
                capabilities[capability.Key] = capability.Value;
            }
        }

        var snapshot = new CapabilitySnapshot
        {
            ProviderId = _providers.Count > 0 ? _providers[0].Id : string.Empty,
            ContractVersion = BDToolContract.Version,
            Capabilities = capabilities,
            Limits = limits
        };
        snapshot.Limits["activity.largeContentThresholdBytes"] =
            ActivityPolicy.LargeContentThresholdBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        snapshot.Limits["activity.retentionDays"] =
            ActivityPolicy.DefaultRetentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return Task.FromResult(snapshot);
    }
}
