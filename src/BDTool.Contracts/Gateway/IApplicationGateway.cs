using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts.Activity;
using BDTool.Contracts.Capabilities;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;

namespace BDTool.Contracts.Gateway
{
    /// <summary>
    /// 表现层与 CLI 的唯一执行入口。可以先用进程内实现，
    /// 后续替换为本地 IPC 时不改变 ViewModel 契约。
    /// </summary>
    public interface IApplicationGateway
    {
        Task<ApplicationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

        Task<OperationResult> ExecuteAsync(
            OperationRequest request,
            ActivityContext activity,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// 调用级审计上下文。ActivityId 由调用边界创建并贯穿 Application 调用。
    /// </summary>
    public sealed class ActivityContext
    {
        public string ActivityId { get; set; } = string.Empty;
        public string TraceId { get; set; } = string.Empty;
        public string? ParentActivityId { get; set; }
        public string Source { get; set; } = ActivitySources.Cli;
        public ActivityActor Actor { get; set; } = new ActivityActor();
    }

    /// <summary>
    /// 应用快照。只包含可公开的环境事实，不包含 Token、私有会话或 Provider 原始响应。
    /// </summary>
    public sealed class ApplicationSnapshot
    {
        public string AppVersion { get; set; } = string.Empty;
        public string ContractVersion { get; set; } = BDToolContract.Version;
        public string ProjectPath { get; set; } = string.Empty;
        public bool ProjectExists { get; set; }
        public List<ProviderDescriptor> Providers { get; set; } = new List<ProviderDescriptor>();
        public CapabilitySnapshot Capabilities { get; set; } = new CapabilitySnapshot();
        public EditorStatus? Editor { get; set; }
        public DateTimeOffset GeneratedAt { get; set; }

        /// <summary>Activity 大内容阈值，供 UI 与诊断展示。</summary>
        public int ActivityArtifactThresholdBytes { get; set; }

        /// <summary>Activity 默认保留天数。</summary>
        public int ActivityRetentionDays { get; set; }
    }
}
