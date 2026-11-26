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
}
