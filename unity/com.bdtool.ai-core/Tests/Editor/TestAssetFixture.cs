using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace BDTool.UnityCore.Tests
{
    /// <summary>
    /// 每个测试独占一个 Assets 下的临时目录，避免路径和 AssetDatabase 缓存互相污染。
    /// Dispose 删除整个用例目录；清理放在 TearDown 中，即使断言失败也会执行。
    /// </summary>
    internal sealed class TestAssetFixture : IDisposable
    {
        private const string RootFolder = "Assets/__BDToolUnityCoreTests__";

        public TestAssetFixture()
        {
            if (!AssetDatabase.IsValidFolder(RootFolder))
            {
                string createdGuid = AssetDatabase.CreateFolder("Assets", "__BDToolUnityCoreTests__");
                if (string.IsNullOrEmpty(createdGuid))
                {
                    throw new InvalidOperationException("无法创建 Unity Core 测试根目录。");
                }
            }

            string caseFolderName = "Case_" + Guid.NewGuid().ToString("N");
            string createdCaseGuid = AssetDatabase.CreateFolder(RootFolder, caseFolderName);
            if (string.IsNullOrEmpty(createdCaseGuid))
            {
                throw new InvalidOperationException("无法创建 Unity Core 测试用例目录。");
            }

            AssetFolderPath = RootFolder + "/" + caseFolderName;
        }

        public string AssetFolderPath { get; }

        /// <summary>创建带指定直接子对象数和根对象激活状态的 Prefab，并强制同步导入。</summary>
        public string CreatePrefab(string fileName, string rootName, int childCount, bool rootActive = true)
        {
            string assetPath = AssetFolderPath + "/" + fileName;
            GameObject root = new GameObject(rootName);
            try
            {
                for (int index = 0; index < childCount; index++)
                {
                    GameObject child = new GameObject("Child_" + index.ToString());
                    child.transform.SetParent(root.transform, false);
                }

                // 根对象 activeSelf 会随 Prefab 保存；测试需要同时覆盖从激活与未激活状态出发。
                root.SetActive(rootActive);
                GameObject savedRoot = PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                if (savedRoot == null)
                {
                    throw new InvalidOperationException("测试 Prefab 保存失败：" + assetPath);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            return assetPath;
        }

        /// <summary>测试侧独立读取原始字节，避免复用被测服务的哈希实现。</summary>
        public static string ComputeFileSha256(string assetPath)
        {
            byte[] bytes = File.ReadAllBytes(ToAbsolutePath(assetPath));
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>测试侧直接读取资产根对象的 activeSelf，避免只依赖被测服务的返回值。</summary>
        public static bool ReadRootActiveSelf(string assetPath)
        {
            GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (root == null)
            {
                throw new InvalidOperationException("测试无法加载 Prefab：" + assetPath);
            }

            return root.activeSelf;
        }

        public static string ToAbsolutePath(string assetPath)
        {
            DirectoryInfo projectRoot = Directory.GetParent(Application.dataPath);
            if (projectRoot == null)
            {
                throw new InvalidOperationException("测试无法解析 Unity 工程根。");
            }

            return Path.Combine(
                projectRoot.FullName,
                assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        public void Dispose()
        {
            AssetDatabase.DeleteAsset(AssetFolderPath);
            if (AssetDatabase.IsValidFolder(RootFolder) && AssetDatabase.GetSubFolders(RootFolder).Length == 0)
            {
                AssetDatabase.DeleteAsset(RootFolder);
            }

            AssetDatabase.Refresh();
        }
    }
}
