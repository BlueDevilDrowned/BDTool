using System.Collections.Generic;

namespace BDTool.Contracts.Projects
{
    /// <summary>
    /// `project.inspect` 的 typed 结果。这里只暴露 BDTool 稳定字段，
    /// 不把 Unity CLI 的原始响应直接传给编排层。
    /// </summary>
    public sealed class ProjectInspectData
    {
        public string ProjectPath { get; set; } = string.Empty;
        public string UnityVersion { get; set; } = string.Empty;
        public string SceneName { get; set; } = string.Empty;
        public bool IsPrefabMode { get; set; }

        /// <summary>
        /// 本次 typed 结果中的递归节点总数（含所有层级的子节点）。
        /// 不采用上游按顶层 root 统计的 objectCount。
        /// </summary>
        public int ObjectCount { get; set; }
        public List<ProjectInspectNode> Hierarchy { get; set; } = new List<ProjectInspectNode>();
    }

    /// <summary>
    /// 场景层级中的单个对象摘要，不携带 Unity 对象引用。
    /// <see cref="Children"/> 递归保存上游 `children`，使层级保持原始父子关系，
    /// 而不是在顶层被压平成一组根节点。
    /// </summary>
    public sealed class ProjectInspectNode
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool Active { get; set; }
        public List<string> Components { get; set; } = new List<string>();
        public List<ProjectInspectNode> Children { get; set; } = new List<ProjectInspectNode>();
    }
}
