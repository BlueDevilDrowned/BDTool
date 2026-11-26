using System;
using Xunit;

namespace BDTool.Tests.Workspace;

/// <summary>
/// 只在 Windows 文件系统语义下成立的契约断言。
/// xUnit v2 没有运行时跳过能力，因此在发现阶段计算 Skip，让测试被统计为“跳过”
/// 而不是执行到一半静默 return 后仍计为通过。
/// </summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "该契约仅适用于 Windows 文件系统语义。";
        }
    }
}

/// <summary>
/// 依赖 reparse point 的测试。发现阶段实际探测当前环境能否创建目录链接
/// （符号链接或 junction），不支持时直接标记为 Skip；这样“环境不具备能力”
/// 会出现在测试报告的跳过统计里，而不是落成一次没有断言的通过。
/// </summary>
internal sealed class ReparsePointFactAttribute : FactAttribute
{
    public ReparsePointFactAttribute()
    {
        if (!WorkspaceReparsePoints.IsSupported)
        {
            Skip = "当前环境无法创建目录符号链接或 junction，reparse point 分支无法断言。";
        }
    }
}
