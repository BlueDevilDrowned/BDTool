using System;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;
using BDTool.Contracts.Workspace;

namespace BDTool.Application.Offline;

/// <summary>
/// 未装配 Workspace Kernel 的占位实现。所有调用都抛出 NotSupportedException，
/// 让越界访问在网关处变成明确的执行失败，而不是空引用或伪造结果。
/// </summary>
internal sealed class UnavailableWorkspaceKernel : IWorkspaceKernel
{
    public static readonly UnavailableWorkspaceKernel Instance = new UnavailableWorkspaceKernel();

    private UnavailableWorkspaceKernel()
    {
    }

    public Task<ProviderDescriptor> DescribeAsync(CancellationToken cancellationToken) => throw Unavailable();

    public Task<WorkspaceReadResult> ReadAsync(WorkspaceReadRequest request, CancellationToken cancellationToken) => throw Unavailable();

    public Task<WorkspaceSearchResult> SearchAsync(WorkspaceSearchRequest request, CancellationToken cancellationToken) => throw Unavailable();

    public Task<WorkspaceFingerprintResult> FingerprintAsync(WorkspaceFingerprintRequest request, CancellationToken cancellationToken) => throw Unavailable();

    public Task<WorkspaceChangeResult> ApplyAsync(WorkspaceChangeRequest request, CancellationToken cancellationToken) => throw Unavailable();

    private static NotSupportedException Unavailable() =>
        new NotSupportedException("M0 未装配 Workspace Kernel；离线 Handler 不得访问 Workspace。");
}

/// <summary>
/// 未装配 Editor Kernel 的占位实现。M0 明确不连接 Unity，
/// 任何 Editor 调用都直接失败，避免误报集成可用。
/// </summary>
internal sealed class UnavailableEditorKernel : IEditorKernel
{
    public static readonly UnavailableEditorKernel Instance = new UnavailableEditorKernel();

    private UnavailableEditorKernel()
    {
    }

    public Task<ProviderDescriptor> DescribeAsync(CancellationToken cancellationToken) => throw Unavailable();

    public Task<EditorStatus> GetStatusAsync(EditorStatusRequest request, CancellationToken cancellationToken) => throw Unavailable();

    public Task<OperationHandle> SubmitAsync(EditorOperationRequest request, CancellationToken cancellationToken) => throw Unavailable();

    public Task<EditorOperationResult> GetResultAsync(OperationHandle handle, CancellationToken cancellationToken) => throw Unavailable();

    public Task CancelAsync(OperationHandle handle, CancellationToken cancellationToken) => throw Unavailable();

    private static NotSupportedException Unavailable() =>
        new NotSupportedException("M0 不连接 Unity；Editor Provider 尚未实现。");
}
