namespace BDTool.Core;

/// <summary>
/// 产品身份的单一来源。程序集名、命名空间与 CLI 名称必须在整个解决方案保持一致，
/// 因此不在这里提供可配置覆盖，避免不同入口对同一产品使用不同标识。
/// </summary>
public static class BDToolIdentity
{
    /// <summary>产品与解决方案名称。</summary>
    public const string ProductName = "BDTool";

    /// <summary>CLI 可执行名，不含扩展名。</summary>
    public const string ExecutableName = "bdtool";

    /// <summary>当前产品版本；里程碑由 <see cref="Milestone"/> 单独表达，不随版本号耦合。</summary>
    public const string Version = "1.0.0";

    /// <summary>当前里程碑标识，用于快照和诊断输出。</summary>
    public const string Milestone = "M5";
}
