namespace BDTool.Provider.Local;

/// <summary>
/// Local Workspace 的实例级限制。默认值对应 M1 冻结契约；ProjectPath 留空时使用
/// LocalWorkspaceKernel 构造函数传入的项目路径。
/// </summary>
public sealed class LocalWorkspaceOptions
{
    /// <summary>1 MiB，与 M1 的读写上限保持一致。</summary>
    public const long DefaultMaxBytes = 1024 * 1024;

    public const int DefaultMaxSearchResults = 200;

    /// <summary>1 MiB，与读取上限保持一致。</summary>
    public const long DefaultMaxApplyBytes = DefaultMaxBytes;

    /// <summary>可选项目根；为空时使用 Kernel 构造参数。</summary>
    public string ProjectPath { get; set; } = string.Empty;

    /// <summary>单次 workspace.read 允许的原始文件字节数。</summary>
    public long MaxReadBytes { get; set; } = DefaultMaxBytes;

    /// <summary>workspace.search 愿意加载的单个文件原始字节数。</summary>
    public long MaxSearchFileBytes { get; set; } = DefaultMaxBytes;

    /// <summary>workspace.search 最多返回的匹配行数。</summary>
    public int MaxSearchResults { get; set; } = DefaultMaxSearchResults;

    /// <summary>
    /// 单次 workspace.apply 允许的编码后内容字节数。默认与读取上限一致，
    /// 避免写入成为绕过 M1 文本边界的通道；dry-run 同样受此限制。
    /// </summary>
    public long MaxApplyBytes { get; set; } = DefaultMaxApplyBytes;
}
