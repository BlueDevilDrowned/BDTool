using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BDTool.UnityCore
{
    /// <summary>
    /// 在 SHA-256 CAS 保护下修改 Prefab 根对象的 activeSelf。
    /// 写入对象状态而非名称，避免 Prefab 根名与文件 stem 耦合影响结果。
    /// 所有步骤同步执行；CAS 校验先于 Undo 和保存，陈旧前置条件不会触达资产写入路径。
    /// </summary>
    public sealed class PrefabActivationService
    {
        private readonly AssetPathGuard _pathGuard;

        public PrefabActivationService()
        {
            _pathGuard = new AssetPathGuard(UnityProjectLayout.ProjectRoot);
        }

        /// <summary>
        /// 校验并可选执行 Prefab 根对象 activeSelf 写入。
        /// dry-run 只返回预览，不创建 Undo 组、不保存、不导入；
        /// 真实写入后强制同步导入并回读校验，校验不通过会抛出 WriteFailed。
        /// </summary>
        public PrefabActivationResult SetActive(
            string assetPath,
            string expectedSha256,
            bool isActive,
            bool dryRun)
        {
            string normalizedAssetPath;
            string fullPath = _pathGuard.ResolvePrefab(assetPath, out normalizedAssetPath);
            ValidateExpectedSha256(expectedSha256);

            if (!File.Exists(fullPath))
            {
                throw new UnityCoreException(
                    UnityCoreErrorCode.AssetNotFound,
                    "要修改 activeSelf 的 Prefab 不存在：" + normalizedAssetPath);
            }

            // 原始字节哈希是唯一 CAS 口径。此次读取与任何 Undo/Save 隔离。
            string previousSha256 = AssetFileFingerprint.ComputeSha256(fullPath);
            if (!string.Equals(previousSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnityCoreException(
                    UnityCoreErrorCode.PreconditionFailed,
                    "Prefab 哈希已变化，拒绝写入。期望 " + expectedSha256 + "，实际 " + previousSha256 + "。");
            }

            GameObject assetRoot = AssetDatabase.LoadAssetAtPath<GameObject>(normalizedAssetPath);
            if (assetRoot == null)
            {
                throw new UnityCoreException(
                    UnityCoreErrorCode.InvalidAsset,
                    "Prefab 文件存在，但 AssetDatabase 未能加载为 GameObject：" + normalizedAssetPath);
            }

            bool wasActive = assetRoot.activeSelf;
            bool changed = wasActive != isActive;

            if (dryRun)
            {
                return CreateResult(
                    normalizedAssetPath,
                    previousSha256,
                    previousSha256,
                    wasActive,
                    isActive,
                    changed,
                    false,
                    true,
                    string.Empty,
                    false);
            }

            if (!changed)
            {
                // 目标状态已经成立。这里不进入保存路径，避免无意义地重写 Prefab 或制造 Undo 记录。
                return CreateResult(
                    normalizedAssetPath,
                    previousSha256,
                    previousSha256,
                    wasActive,
                    isActive,
                    false,
                    false,
                    false,
                    string.Empty,
                    false);
            }

            int undoGroupIndex = -1;
            GameObject prefabContents = null;
            try
            {
                prefabContents = PrefabUtility.LoadPrefabContents(normalizedAssetPath);
                if (prefabContents == null)
                {
                    throw new UnityCoreException(
                        UnityCoreErrorCode.InvalidAsset,
                        "无法加载 Prefab 内容以修改 activeSelf：" + normalizedAssetPath);
                }

                // Undo 组在 CAS 校验通过后才创建；失败前置条件不会留下空组。
                Undo.IncrementCurrentGroup();
                undoGroupIndex = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("BDTool Set Prefab Root Active: " + normalizedAssetPath);
                Undo.RecordObject(prefabContents, "Set Prefab Root Active");

                prefabContents.SetActive(isActive);
                GameObject savedRoot = PrefabUtility.SaveAsPrefabAsset(prefabContents, normalizedAssetPath);
                if (savedRoot == null)
                {
                    throw new UnityCoreException(
                        UnityCoreErrorCode.WriteFailed,
                        "SaveAsPrefabAsset 未返回保存后的根对象：" + normalizedAssetPath);
                }
            }
            catch (UnityCoreException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new UnityCoreException(
                    UnityCoreErrorCode.WriteFailed,
                    "保存 Prefab 失败：" + normalizedAssetPath,
                    exception);
            }
            finally
            {
                if (prefabContents != null)
                {
                    PrefabUtility.UnloadPrefabContents(prefabContents);
                }
            }

            try
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(
                    normalizedAssetPath,
                    ImportAssetOptions.ForceSynchronousImport);
            }
            catch (Exception exception)
            {
                throw new UnityCoreException(
                    UnityCoreErrorCode.WriteFailed,
                    "Prefab 已保存，但同步导入或资产数据库提交失败：" + normalizedAssetPath,
                    exception);
            }

            GameObject readBackRoot = AssetDatabase.LoadAssetAtPath<GameObject>(normalizedAssetPath);
            bool? readBackActive = readBackRoot == null ? (bool?)null : readBackRoot.activeSelf;
            string newSha256 = AssetFileFingerprint.ComputeSha256(fullPath);
            bool shaChanged = !string.Equals(newSha256, previousSha256, StringComparison.OrdinalIgnoreCase);
            bool verified = readBackActive.HasValue && readBackActive.Value == isActive && shaChanged;

            Undo.CollapseUndoOperations(undoGroupIndex);

            if (!verified)
            {
                // 已落盘但无法证明目标状态生效时必须让调用方看到失败，不能返回 verified=false 的“成功”。
                throw new UnityCoreException(
                    UnityCoreErrorCode.WriteFailed,
                    "Prefab 已保存，但回读复核失败：目标 activeSelf=" + FormatBool(isActive) +
                    "，实际=" + (readBackActive.HasValue ? FormatBool(readBackActive.Value) : "<无法加载>") +
                    "，哈希是否变化=" + FormatBool(shaChanged) + "。路径：" + normalizedAssetPath);
            }

            return CreateResult(
                normalizedAssetPath,
                previousSha256,
                newSha256,
                wasActive,
                isActive,
                true,
                true,
                false,
                undoGroupIndex.ToString(CultureInfo.InvariantCulture),
                true);
        }

        private static void ValidateExpectedSha256(string expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                throw new UnityCoreException(
                    UnityCoreErrorCode.InvalidArgument,
                    "expectedSha256 必填，且不能为空或空白。");
            }
        }

        private static string FormatBool(bool value)
        {
            return value ? "true" : "false";
        }

        private static PrefabActivationResult CreateResult(
            string assetPath,
            string previousSha256,
            string newSha256,
            bool wasActive,
            bool isActive,
            bool changed,
            bool saved,
            bool dryRun,
            string undoGroup,
            bool verified)
        {
            return new PrefabActivationResult
            {
                AssetPath = assetPath,
                PreviousSha256 = previousSha256,
                NewSha256 = newSha256,
                WasActive = wasActive,
                IsActive = isActive,
                Changed = changed,
                Saved = saved,
                DryRun = dryRun,
                UndoGroup = undoGroup,
                Verified = verified
            };
        }
    }
}
