using System;
using BDTool.Contracts.Artifacts;
using BDTool.Contracts.Capabilities;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Workspace;

namespace BDTool.Application.Offline;

/// <summary>
/// M0 的受控操作上下文工厂。Artifact 与能力检查使用真实装配的端口；
/// Workspace/Editor 尚未实现，返回的 Kernel 一旦被访问就显式失败，
/// 从而保证离线 Handler 不会在缺少基础设施时伪造成功。
/// </summary>
public sealed class OfflineOperationContextFactory : IOperationContextFactory
{
    private readonly IArtifactStore _artifacts;
    private readonly ICapabilityInspector _capabilities;

    public OfflineOperationContextFactory(IArtifactStore artifacts, ICapabilityInspector capabilities)
    {
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    }

    /// <inheritdoc />
    public IOperationContext? TryCreate(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new OfflineOperationContext(_artifacts, _capabilities);
    }

    private sealed class OfflineOperationContext : IOperationContext
    {
        private readonly IArtifactStore _artifacts;
        private readonly ICapabilityInspector _capabilities;

        public OfflineOperationContext(IArtifactStore artifacts, ICapabilityInspector capabilities)
        {
            _artifacts = artifacts;
            _capabilities = capabilities;
        }

        /// <summary>M0 未装配 Workspace Provider；访问即抛出明确的不支持错误。</summary>
        public IWorkspaceKernel Workspace { get; } = UnavailableWorkspaceKernel.Instance;

        /// <summary>M0 不连接 Unity；Editor Provider 属于后续里程碑。</summary>
        public IEditorKernel Editor { get; } = UnavailableEditorKernel.Instance;

        public IArtifactStore Artifacts => _artifacts;

        public ICapabilityInspector Capabilities => _capabilities;
    }
}
