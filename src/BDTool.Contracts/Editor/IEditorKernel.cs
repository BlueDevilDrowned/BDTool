using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;

namespace BDTool.Contracts.Editor
{
    /// <summary>
    /// Editor 状态的唯一入口。所有 Unity 写操作必须经过 Provider，业务层不得直接启动 Unity。
    /// </summary>
    public interface IEditorKernel
    {
        Task<ProviderDescriptor> DescribeAsync(CancellationToken cancellationToken);
        Task<EditorStatus> GetStatusAsync(EditorStatusRequest request, CancellationToken cancellationToken);
        Task<OperationHandle> SubmitAsync(EditorOperationRequest request, CancellationToken cancellationToken);
        Task<EditorOperationResult> GetResultAsync(OperationHandle handle, CancellationToken cancellationToken);
        Task CancelAsync(OperationHandle handle, CancellationToken cancellationToken);
    }

    public sealed class EditorStatusRequest
    {
        public string ProjectPath { get; set; } = string.Empty;
    }

    public sealed class EditorStatus
    {
        /// <summary>稳定状态，例如 not-found、starting、ready、busy、disconnected。</summary>
        public string State { get; set; } = "unknown";
        public string ProjectPath { get; set; } = string.Empty;
        public string UnityVersion { get; set; } = string.Empty;
        public int? ProcessId { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Editor 操作请求。包含目标项目、操作 ID、输入、写入范围、期望指纹、超时与 dry-run。
    /// </summary>
    public sealed class EditorOperationRequest
    {
        public string ProjectPath { get; set; } = string.Empty;
        public string OperationId { get; set; } = string.Empty;
        public string InputJson { get; set; } = "{}";
        public List<string> WriteScope { get; set; } = new List<string>();
        public string? ExpectedFingerprint { get; set; }
        public int TimeoutSeconds { get; set; } = 60;
        public bool DryRun { get; set; }
    }

    /// <summary>
    /// Editor 执行结果。OutputJson 是 Provider 已转换为公共契约后的 JSON 文本，
    /// 不是上游原始协议响应。
    /// </summary>
    public sealed class EditorOperationResult
    {
        public OperationHandle Handle { get; set; } = new OperationHandle();
        public OperationStatus Status { get; set; } = OperationStatus.Queued;
        public string OutputJson { get; set; } = "{}";
        public List<KernelError> Errors { get; set; } = new List<KernelError>();
    }
}
