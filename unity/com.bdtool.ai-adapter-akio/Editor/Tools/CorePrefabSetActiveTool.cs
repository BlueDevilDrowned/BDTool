using System;
using BDTool.UnityCore;
using UnityCliBridge.Tools;

namespace BDTool.UnityAdapter.Akio
{
    /// <summary>
    /// 把 Core 的受控 Prefab 根对象激活状态写入服务暴露为 unity-cli custom tool。
    /// Adapter 只做参数 null 防护、同步调用转发和 camelCase 结果整形；
    /// 路径、CAS、activeSelf、Undo、保存与回读规则均由
    /// <see cref="PrefabActivationService"/> 维护。
    /// </summary>
    public static class CorePrefabSetActiveTool
    {
        /// <summary>
        /// 在 expectedSha256 前置条件下设置 Prefab 根对象的 activeSelf。
        /// dryRun 为 true 时由 Core 只做校验与预览；异常不在此捕获，由 bridge 统一映射。
        /// </summary>
        [UnityCliTool(
            "bdtool_core_prefab_set_active",
            Description = "Set a prefab root's activeSelf through BDTool Unity Core with SHA-256 CAS and optional dry-run.",
            Mutating = true)]
        public static object SetActive(
            [UnityCliArg("Assets/ 下的 Prefab 资产路径。")] string assetPath,
            [UnityCliArg("调用方观察到的 Prefab 原始字节 SHA-256；Core 做大小写不敏感比较。")] string expectedSha256,
            [UnityCliArg("Prefab 根对象的目标 activeSelf 状态。")] bool isActive,
            [UnityCliArg("true 时只校验并返回预览，不创建 Undo、不保存。")] bool dryRun)
        {
            if (assetPath == null)
            {
                throw new ArgumentNullException(nameof(assetPath));
            }

            if (expectedSha256 == null)
            {
                throw new ArgumentNullException(nameof(expectedSha256));
            }

            PrefabActivationResult result = new PrefabActivationService().SetActive(
                assetPath,
                expectedSha256,
                isActive,
                dryRun);
            return new
            {
                assetPath = result.AssetPath,
                previousSha256 = result.PreviousSha256,
                newSha256 = result.NewSha256,
                wasActive = result.WasActive,
                isActive = result.IsActive,
                changed = result.Changed,
                saved = result.Saved,
                dryRun = result.DryRun,
                undoGroup = result.UndoGroup,
                verified = result.Verified
            };
        }
    }
}
