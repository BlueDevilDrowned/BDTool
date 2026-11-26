using System;
using BDTool.UnityCore;
using UnityCliBridge.Tools;

namespace BDTool.UnityAdapter.Akio
{
    /// <summary>
    /// 把 Core 的只读资产快照服务暴露为 unity-cli custom tool。
    /// 适配层只做 null 防护、调用转发与 camelCase 结果整形；路径、资产类型和
    /// 哈希规则由 <see cref="AssetSnapshotService"/> 负责。
    /// </summary>
    public static class CoreReadTool
    {
        /// <summary>
        /// 读取指定 Prefab 的稳定快照。
        /// 空字符串是否合法由 Core 判定；Core 异常不在此捕获，交给 bridge 统一映射。
        /// </summary>
        [UnityCliTool(
            "bdtool_core_read",
            Description = "Read a BDTool Unity Core asset snapshot for an Assets/ prefab.",
            Mutating = false)]
        public static object Read(
            [UnityCliArg("Assets/ 下的 Prefab 资产路径。")] string assetPath)
        {
            if (assetPath == null)
            {
                throw new ArgumentNullException(nameof(assetPath));
            }

            AssetSnapshot snapshot = new AssetSnapshotService().Read(assetPath);
            return new
            {
                assetPath = snapshot.AssetPath,
                exists = snapshot.Exists,
                sha256 = snapshot.Sha256,
                rootObjectName = snapshot.RootObjectName,
                childCount = snapshot.ChildCount,
                unityVersion = snapshot.UnityVersion
            };
        }
    }
}
