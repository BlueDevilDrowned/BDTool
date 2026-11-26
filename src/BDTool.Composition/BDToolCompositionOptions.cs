using BDTool.Core;

namespace BDTool.Composition;

/// <summary>
/// 组合根输入。所有可选值都有明确默认，调用方不需要了解内部实现。
/// </summary>
public sealed class BDToolCompositionOptions
{
    /// <summary>操作针对的项目路径；为空时使用当前工作目录。</summary>
    public string ProjectPath { get; init; } = string.Empty;

    /// <summary>操作目录文件路径；为空时使用输出目录下的 catalog/operations.json。</summary>
    public string? CatalogPath { get; init; }

    /// <summary>应用版本，默认取 Core 中冻结的 BDTool 版本。</summary>
    public string AppVersion { get; init; } = BDToolIdentity.Version;

    /// <summary>
    /// 可选 unity-cli 可执行文件。为空时依次读取 BDTOOL_UNITY_CLI_PATH、
    /// 当前目录及祖先目录下的 .tools/unity-cli，最后只读 PATH 兜底。
    /// 显式给出但文件不存在时不静默回退，Editor Provider 保持未装配。
    /// </summary>
    public string? UnityCliPath { get; init; }

    /// <summary>
    /// 可选 Unity Editor 路径，供 unity-cli 启动编辑器时使用；为空时读取
    /// BDTOOL_UNITY_EDITOR_PATH / UNITY_EDITOR_PATH。只读取环境，不修改 PATH。
    /// </summary>
    public string? UnityEditorPath { get; init; }

    /// <summary>Editor 子进程默认超时；必须大于 0。</summary>
    public int EditorTimeoutMilliseconds { get; init; } = 60_000;
}
