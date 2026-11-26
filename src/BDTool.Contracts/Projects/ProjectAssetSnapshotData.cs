namespace BDTool.Contracts.Projects
{
    /// <summary>
    /// Unity Core 只读资产快照。只暴露稳定资产事实，不携带 Unity 对象引用或
    /// Akio/Unity-CLI 私有 DTO。
    /// </summary>
    public sealed class ProjectAssetSnapshotData
    {
        public string ProjectPath { get; set; } = string.Empty;
        public string AssetPath { get; set; } = string.Empty;
        public bool Exists { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string RootObjectName { get; set; } = string.Empty;
        public int ChildCount { get; set; }
        public string UnityVersion { get; set; } = string.Empty;
    }
}
