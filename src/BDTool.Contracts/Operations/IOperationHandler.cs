using System.Threading;
using System.Threading.Tasks;

namespace BDTool.Contracts.Operations
{
    /// <summary>
    /// 操作执行入口。Handler 只能通过 <see cref="IOperationContext"/> 使用基础设施，
    /// 不得接触 Process、HttpClient、Provider 原始 DTO、UnityEditor 或 Codex Agent。
    /// </summary>
    public interface IOperationHandler
    {
        string OperationId { get; }

        Task<OperationResult> ExecuteAsync(
            OperationRequest request,
            IOperationContext context,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Handler 可用的受控端口集合。新增基础设施必须通过此边界扩展。
    /// </summary>
    public interface IOperationContext
    {
        Workspace.IWorkspaceKernel Workspace { get; }
        Editor.IEditorKernel Editor { get; }
        Artifacts.IArtifactStore Artifacts { get; }
        Capabilities.ICapabilityInspector Capabilities { get; }
    }
}
