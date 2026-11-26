using System;
using System.IO;

namespace BDTool.Tests.Support;

/// <summary>
/// CLI 与组合测试使用的临时项目目录。每个实例独立，Dispose 时递归删除，
/// 避免测试之间通过 .bdtool 状态互相干扰。
/// </summary>
internal sealed class TemporaryProjectDirectory : IDisposable
{
    public TemporaryProjectDirectory()
    {
        ProjectPath = Path.Combine(
            Path.GetTempPath(),
            "bdtool-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(ProjectPath);
    }

    public string ProjectPath { get; }

    public string ActivityRoot => Path.Combine(ProjectPath, ".bdtool", "state", "activity");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(ProjectPath))
            {
                Directory.Delete(ProjectPath, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
