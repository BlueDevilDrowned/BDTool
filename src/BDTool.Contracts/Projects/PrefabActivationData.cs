namespace BDTool.Contracts.Projects
{
    /// <summary>
    /// 受控 Prefab 根对象启用状态写入结果。Previous/NewSha256 与 Workspace
    /// 文件指纹使用同一口径；IsActive 必须来自保存后的重新加载。
    /// </summary>
    public sealed class PrefabActivationData
    {
        public string ProjectPath { get; set; } = string.Empty;
        public string AssetPath { get; set; } = string.Empty;
        public string PreviousSha256 { get; set; } = string.Empty;
        public string NewSha256 { get; set; } = string.Empty;
        public bool WasActive { get; set; }
        public bool IsActive { get; set; }
        public bool Changed { get; set; }
        public bool Saved { get; set; }
        public bool DryRun { get; set; }
        public string UndoGroup { get; set; } = string.Empty;
        public bool Verified { get; set; }
    }
}
