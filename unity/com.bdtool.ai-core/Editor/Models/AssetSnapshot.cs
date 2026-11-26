using System;

namespace BDTool.UnityCore
{
    /// <summary>
    /// Prefab 的只读事实快照。哈希来自磁盘原始字节，对象字段来自 Unity 已导入资产；
    /// 两者都保留，便于调用方发现“文件存在但仍未正确导入”的异常状态。
    /// </summary>
    [Serializable]
    public sealed class AssetSnapshot
    {
        public string AssetPath { get; set; } = string.Empty;

        public bool Exists { get; set; }

        public string Sha256 { get; set; } = string.Empty;

        public string RootObjectName { get; set; } = string.Empty;

        public int ChildCount { get; set; }

        public string UnityVersion { get; set; } = string.Empty;
    }
}
