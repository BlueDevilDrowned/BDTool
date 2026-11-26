using System.Threading;
using System.Threading.Tasks;

namespace BDTool.Operations.Diagnostics;

/// <summary>
/// doctor 所需的本机环境探测端口。实现位于 Application，Handler 只消费结果，
/// 不直接接触文件系统 API，保持操作层可测试且职责单纯。
/// </summary>
public interface IDoctorEnvironmentProbe
{
    Task<DoctorEnvironmentSnapshot> ProbeAsync(string projectPath, CancellationToken cancellationToken);
}

/// <summary>
/// 环境探测快照。所有布尔值只表示探测当下的事实；错误详情必须已脱敏。
/// </summary>
public sealed class DoctorEnvironmentSnapshot
{
    public string ProjectPath { get; set; } = string.Empty;
    public bool ProjectExists { get; set; }
    public string ProjectDetail { get; set; } = string.Empty;
    public string StateRoot { get; set; } = string.Empty;
    public bool StateRootWritable { get; set; }
    public string StateRootDetail { get; set; } = string.Empty;
    public string ActivityRoot { get; set; } = string.Empty;
    public bool ActivityRootWritable { get; set; }
    public string ActivityRootDetail { get; set; } = string.Empty;
}
