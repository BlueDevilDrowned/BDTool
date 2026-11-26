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

    /// <summary>M1 Editor 占位。禁止在没有真实连接时把 Connected 设为 true。</summary>
    public sealed class ProjectEditorContext
    {
        public bool Connected { get; set; }
        public string Status { get; set; } = "not-connected";
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>项目上下文公开的 Activity 限制。</summary>
    public sealed class ProjectActivityLimits
    {
        public int LargeContentThresholdBytes { get; set; }
        public int RetentionDays { get; set; }
    }
}
