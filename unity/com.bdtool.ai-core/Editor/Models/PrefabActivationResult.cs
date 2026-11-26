using System;

namespace BDTool.UnityCore
{
    /// <summary>
    /// Prefab 根对象 activeSelf 写入的可验证结果。
    /// </summary>
    /// <remarks>
    /// <see cref="IsActive"/> 表示本次请求的目标状态；真实写入成功后它与强制同步导入后的回读状态一致，
    /// 否则服务会抛出 <see cref="UnityCoreException"/>，不会返回 verified=false 的成功结果。
    /// dry-run 和状态未变化时不会产生新字节，因此 <see cref="NewSha256"/> 等于 <see cref="PreviousSha256"/>。
    /// </remarks>
    [Serializable]
    public sealed class PrefabActivationResult
    {
        public string AssetPath { get; set; } = string.Empty;

        public string PreviousSha256 { get; set; } = string.Empty;

        public string NewSha256 { get; set; } = string.Empty;

        public bool WasActive { get; set; }

        public bool IsActive { get; set; }

        public bool Changed { get; set; }

        public bool Saved { get; set; }

        public bool DryRun { get; set; }

        /// <summary>Unity Undo 组序号；未创建 Undo 组时为空字符串。</summary>
        public string UndoGroup { get; set; } = string.Empty;

        /// <summary>实际保存路径是否已通过重新加载状态与哈希变化复核。</summary>
        public bool Verified { get; set; }
    }
}
