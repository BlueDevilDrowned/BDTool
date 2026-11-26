using System.Collections.Generic;
using BDTool.Contracts.Artifacts;
using BDTool.Contracts.Errors;

namespace BDTool.Contracts.Operations
{
    /// <summary>
    /// 统一结果信封。Data 只能由 <see cref="OperationResult{TData}"/> 提供 typed DTO，
    /// 禁止在公共边界暴露万能 object 业务结果。
    /// </summary>
    public class OperationResult
    {
        public OperationStatus Status { get; set; } = OperationStatus.Failed;
        public List<string> ChangedAssets { get; set; } = new List<string>();
        public List<KernelWarning> Warnings { get; set; } = new List<KernelWarning>();
        public List<KernelError> Errors { get; set; } = new List<KernelError>();
        public List<ArtifactReference> Artifacts { get; set; } = new List<ArtifactReference>();
        public ProofData Proof { get; set; } = new ProofData();
        public NextAction? Next { get; set; }
    }

    /// <summary>带明确业务 DTO 的操作结果。</summary>
    public sealed class OperationResult<TData> : OperationResult
    {
        public TData Data { get; set; } = default!;
    }

    /// <summary>
    /// 可验证证据。值均使用字符串，避免证据格式反过来污染 Contracts。
    /// </summary>
    public sealed class ProofData
    {
        public string Algorithm { get; set; } = "sha256";
        public string InputFingerprint { get; set; } = string.Empty;
        public string OutputFingerprint { get; set; } = string.Empty;
        public Dictionary<string, string> Evidence { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>异步 Job 的稳定句柄。</summary>
    public sealed class OperationHandle
    {
        public string JobId { get; set; } = string.Empty;
        public string OperationId { get; set; } = string.Empty;
        public OperationStatus Status { get; set; } = OperationStatus.Queued;
    }
}
