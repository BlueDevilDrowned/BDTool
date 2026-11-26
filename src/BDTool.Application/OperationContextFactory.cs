using System;
using BDTool.Application.Offline;
using BDTool.Contracts.Artifacts;
using BDTool.Contracts.Capabilities;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Workspace;

namespace BDTool.Application;

/// <summary>
/// 真实操作上下文工厂。Workspace/Editor Kernel 由组合根绑定到项目根；
/// 未装配 Editor 时保留访问即失败的显式占位，避免离线 Handler 伪造连接。
/// </summary>
public sealed class OperationContextFactory : IOperationContextFactory
{
    private readonly IWorkspaceKernel _workspace;
    private readonly IArtifactStore _artifacts;
    private readonly ICapabilityInspector _capabilities;
    private readonly Contracts.Editor.IEditorKernel _editor;

    public OperationContextFactory(
        IWorkspaceKernel workspace,
        IArtifactStore artifacts,
        ICapabilityInspector capabilities,
        Contracts.Editor.IEditorKernel? editor = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _editor = editor ?? UnavailableEditorKernel.Instance;
    }

    /// <inheritdoc />
    public IOperationContext? TryCreate(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new OperationContext(_workspace, _editor, _artifacts, _capabilities);
    }

    private sealed class OperationContext : IOperationContext
    {
        public OperationContext(
            IWorkspaceKernel workspace,
            Contracts.Editor.IEditorKernel editor,
            IArtifactStore artifacts,
            ICapabilityInspector capabilities)
        {
            Workspace = workspace;
            Editor = editor;
            Artifacts = artifacts;
            Capabilities = capabilities;
        }

        public IWorkspaceKernel Workspace { get; }

        public Contracts.Editor.IEditorKernel Editor { get; }

        public IArtifactStore Artifacts { get; }

        public ICapabilityInspector Capabilities { get; }
    }
}
