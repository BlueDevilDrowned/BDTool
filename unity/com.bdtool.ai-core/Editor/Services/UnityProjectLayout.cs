using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BDTool.UnityCore
{
    /// <summary>
    /// 解析当前 Editor 的项目根。服务不接收外部项目根，避免调用方把“资产路径校验”
    /// 与“实际执行 Editor 的工程”分离，从而绕过项目边界。
    /// </summary>
    internal static class UnityProjectLayout
    {
        public static string ProjectRoot
        {
            get
            {
                string assetsPath = Application.dataPath;
                if (string.IsNullOrWhiteSpace(assetsPath))
                {
                    throw new UnityCoreException(
                        UnityCoreErrorCode.InvalidAsset,
                        "无法从 Application.dataPath 获取当前 Unity 工程路径。");
                }

                DirectoryInfo parent = Directory.GetParent(Path.GetFullPath(assetsPath));
                if (parent == null)
                {
                    throw new UnityCoreException(
                        UnityCoreErrorCode.InvalidAsset,
                        "Application.dataPath 没有可用的父目录：" + assetsPath);
                }

                return parent.FullName;
            }
        }
    }
}
