using System.IO;
using UnityEditor;
using UnityEngine;

namespace BDTool.UnityCore
{
    /// <summary>
    /// 读取当前 Unity 工程内 Prefab 的稳定快照。
    /// 服务只读磁盘和 AssetDatabase，不修改资产、Undo 或导入状态。
    /// </summary>
    public sealed class AssetSnapshotService
    {
        private readonly AssetPathGuard _pathGuard;

        public AssetSnapshotService()
        {
            _pathGuard = new AssetPathGuard(UnityProjectLayout.ProjectRoot);
        }

        /// <summary>
        /// 读取 Assets/ 下指定 Prefab。
        /// 路径合法但文件缺失时返回 <see cref="AssetSnapshot.Exists"/> 为 false；
        /// 路径非法或文件存在但无法作为 Prefab 加载时抛出 <see cref="UnityCoreException"/>。
        /// </summary>
        public AssetSnapshot Read(string assetPath)
        {
            string normalizedAssetPath;
            string fullPath = _pathGuard.ResolvePrefab(assetPath, out normalizedAssetPath);

            if (!File.Exists(fullPath))
            {
                return new AssetSnapshot
                {
                    AssetPath = normalizedAssetPath,
                    Exists = false,
                    Sha256 = string.Empty,
                    RootObjectName = string.Empty,
                    ChildCount = 0,
                    UnityVersion = Application.unityVersion ?? string.Empty
                };
            }

            GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(normalizedAssetPath);
            if (root == null)
            {
                throw new UnityCoreException(
                    UnityCoreErrorCode.InvalidAsset,
                    "Prefab 文件存在，但 AssetDatabase 未能加载为 GameObject：" + normalizedAssetPath);
            }

            return new AssetSnapshot
            {
                AssetPath = normalizedAssetPath,
                Exists = true,
                Sha256 = AssetFileFingerprint.ComputeSha256(fullPath),
                RootObjectName = root.name,
                ChildCount = root.transform.childCount,
                UnityVersion = Application.unityVersion ?? string.Empty
            };
        }
    }
}
