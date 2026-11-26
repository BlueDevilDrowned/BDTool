using System;
using System.IO;

namespace BDTool.UnityCore
{
    /// <summary>
    /// 把调用方提供的 Unity 资产路径解析为当前工程内的绝对路径。
    /// 这里先拒绝可疑路径段，再交给 Path.GetFullPath 规范化，最后用 Assets 根前缀做二次包含检查。
    /// 双重检查用于防止绝对路径、Windows 分隔符或规范化后仍越出 Assets 的输入。
    /// </summary>
    internal sealed class AssetPathGuard
    {
#if UNITY_EDITOR_WIN
        private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
#else
        private static readonly StringComparison PathComparison = StringComparison.Ordinal;
#endif

        private readonly string _projectRoot;
        private readonly string _assetsRoot;
        private readonly string _assetsRootPrefix;

        public AssetPathGuard(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                throw new UnityCoreException(UnityCoreErrorCode.InvalidAsset, "Unity 工程根不能为空。");
            }

            _projectRoot = Path.GetFullPath(projectRoot);
            _assetsRoot = Path.GetFullPath(Path.Combine(_projectRoot, "Assets"));
            _assetsRootPrefix = _assetsRoot + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// 返回 Prefab 的绝对文件路径，同时输出以正斜杠表示的规范资产路径。
        /// 仅做文件系统与路径边界校验，不读取资产内容。
        /// </summary>
        public string ResolvePrefab(string assetPath, out string normalizedAssetPath)
        {
            normalizedAssetPath = string.Empty;

            if (string.IsNullOrWhiteSpace(assetPath))
            {
                throw InvalidPath("资产路径不能为空。", assetPath, null);
            }

            if (ContainsControlCharacter(assetPath) || Path.IsPathRooted(assetPath))
            {
                throw InvalidPath("资产路径必须是 Assets/ 下的相对路径。", assetPath, null);
            }

            string portablePath = assetPath.Replace('\\', '/');
            string[] segments = portablePath.Split('/');
            if (segments.Length < 2)
            {
                throw InvalidPath("资产路径至少需要 Assets/ 和一个文件名。", assetPath, null);
            }

            for (int index = 0; index < segments.Length; index++)
            {
                string segment = segments[index];
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    throw InvalidPath("资产路径不能包含空段、. 或 ..。", assetPath, null);
                }
            }

            if (!string.Equals(segments[0], "Assets", StringComparison.Ordinal))
            {
                throw InvalidPath("资产路径必须以 Assets/ 开头。", assetPath, null);
            }

            if (!segments[segments.Length - 1].EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnityCoreException(
                    UnityCoreErrorCode.NotPrefab,
                    "只允许操作 .prefab 资产：" + assetPath);
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(_projectRoot, portablePath));
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is NotSupportedException ||
                exception is PathTooLongException)
            {
                throw InvalidPath("资产路径无法规范化。", assetPath, exception);
            }

            if (!fullPath.StartsWith(_assetsRootPrefix, PathComparison))
            {
                throw InvalidPath("资产路径规范化后越出了 Assets/。", assetPath, null);
            }

            normalizedAssetPath = string.Join("/", segments);
            return fullPath;
        }

        private static bool ContainsControlCharacter(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                if (char.IsControl(value[index]))
                {
                    return true;
                }
            }

            return false;
        }

        private static UnityCoreException InvalidPath(string reason, string assetPath, Exception innerException)
        {
            string message = reason + " 输入：<" + (assetPath ?? "null") + ">";
            return innerException == null
                ? new UnityCoreException(UnityCoreErrorCode.InvalidArgument, message)
                : new UnityCoreException(UnityCoreErrorCode.InvalidArgument, message, innerException);
        }
    }
}
