using System;
using BDTool.Application.Offline;
using BDTool.Contracts.Artifacts;
using BDTool.Contracts.Capabilities;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Workspace;

namespace BDTool.Application;

/// <summary>
/// M1 的真实操作上下文工厂。Workspace Kernel 由组合根绑定到项目根，
/// Editor 在本里程碑仍使用显式不可用实现；Artifact 与能力快照使用同一装配实例。
/// </summary>
public sealed class OperationContextFactory : IOperationContextFactory
{
    private readonly IWorkspaceKernel _workspace;
    private readonly IArtifactStore _artifacts;
    private readonly ICapabilityInspector _capabilities;

    public OperationContextFactory(
        IWorkspaceKernel workspace,
        IArtifactStore artifacts,
        ICapabilityInspector capabilities)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    }

    /// <inheritdoc />
    public IOperationContext? TryCreate(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new OperationContext(_workspace, _artifacts, _capabilities);
    }

    private sealed class OperationContext : IOperationContext
    {
        public OperationContext(
            IWorkspaceKernel workspace,
            IArtifactStore artifacts,
            ICapabilityInspector capabilities)
        {
            Workspace = workspace;
            Artifacts = artifacts;
            Capabilities = capabilities;
        }

        public IWorkspaceKernel Workspace { get; }

        /// <summary>M1 不连接 Unity，Editor 访问必须保持显式失败。</summary>
        public Contracts.Editor.IEditorKernel Editor { get; } = UnavailableEditorKernel.Instance;

        public IArtifactStore Artifacts { get; }

        public ICapabilityInspector Capabilities { get; }
    }
}
