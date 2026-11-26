namespace BDTool.Contracts.Projects
{
    /// <summary>
    /// `project.probe` 的 typed 结果，对应当前会话中自定义只读工具
    /// `bdtool_read_only_probe` 的输出。这里只暴露 BDTool 稳定字段，
    /// 不携带上游原始响应或 Unity 对象引用。
    /// </summary>
    public sealed class ProjectProbeData
    {
        /// <summary>探针所在产品标识（上游 product）。</summary>
        public string Product { get; set; } = string.Empty;
        public string UnityVersion { get; set; } = string.Empty;
        public string Platform { get; set; } = string.Empty;
        public string SceneName { get; set; } = string.Empty;
        public string ScenePath { get; set; } = string.Empty;
        public bool SceneIsDirty { get; set; }
        public int RootObjectCount { get; set; }

        /// <summary>上游观测时间，保持 ISO-8601 字符串原样，不在此处做时区换算。</summary>
        public string ObservedAtUtc { get; set; } = string.Empty;
    }
}
