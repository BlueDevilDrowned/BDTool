using System.Collections.Generic;
using BDTool.Contracts.Capabilities;
using BDTool.Contracts.Providers;

namespace BDTool.Contracts.Projects
{
    /// <summary>
    /// M1 离线项目上下文。它只报告 Workspace 能观察到的事实；
    /// Editor、场景与选中对象在缺少真实 Editor Provider 时必须保持未连接。
    /// </summary>
    public sealed class ProjectContextData
    {
        public string ProjectPath { get; set; } = string.Empty;
        public bool ProjectExists { get; set; }

        /// <summary>从 ProjectVersion.txt 读取的 Unity 版本；无法读取时为 null。</summary>
        public string? UnityVersion { get; set; }

        public ProjectEditorContext Editor { get; set; } = new ProjectEditorContext();
        public List<ProviderDescriptor> Providers { get; set; } = new List<ProviderDescriptor>();
        public CapabilitySnapshot Capabilities { get; set; } = new CapabilitySnapshot();
        public ProjectActivityLimits ActivityLimits { get; set; } = new ProjectActivityLimits();
    }

    /// <summary>
    /// Editor 的可观察状态。只有在真实 Provider 报告 ready 时 Connected 才能为 true；
    /// ProviderId、进程号与 Unity 版本来自公开描述，不伪造场景内容。
    /// </summary>
    public sealed class ProjectEditorContext
    {
        public bool Connected { get; set; }
        public string Status { get; set; } = "not-connected";
        public string Message { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public string? UnityVersion { get; set; }
        public int? ProcessId { get; set; }
    }

    /// <summary>项目上下文公开的 Activity 限制。</summary>
    public sealed class ProjectActivityLimits
    {
        public int LargeContentThresholdBytes { get; set; }
        public int RetentionDays { get; set; }
    }
}
