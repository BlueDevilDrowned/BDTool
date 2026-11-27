using System.Collections.Generic;

namespace BDTool.Contracts.Agents
{
    /// <summary>项目级 Codex Agent Profile 安装结果。</summary>
    public sealed class AgentProfileInstallResult
    {
        public string Scope { get; set; } = "project";
        public string ProjectPath { get; set; } = string.Empty;
        public string AgentsRoot { get; set; } = string.Empty;
        public List<AgentProfileInstallItem> Profiles { get; set; } =
            new List<AgentProfileInstallItem>();
        public List<AgentProfileInstallConflict> Conflicts { get; set; } =
            new List<AgentProfileInstallConflict>();
    }

    public sealed class AgentProfileInstallItem
    {
        public string Name { get; set; } = string.Empty;
        public string DestinationPath { get; set; } = string.Empty;
        public bool Updated { get; set; }
        public bool Managed { get; set; }
    }

    public sealed class AgentProfileInstallConflict
    {
        public string Name { get; set; } = string.Empty;
        public string DestinationPath { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }
}
