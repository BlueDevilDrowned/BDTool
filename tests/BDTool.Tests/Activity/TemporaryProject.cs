using System;
using System.IO;

namespace BDTool.Tests.Activity;

/// <summary>
/// 每个测试独立的临时项目目录。测试结束删除，避免共享状态影响并发用例。
/// </summary>
internal sealed class TemporaryProject : IDisposable
{
    public TemporaryProject()
    {
        ProjectPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "bdtool-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(ProjectPath);
    }

    public string ProjectPath { get; }

    public string ActivityRoot => System.IO.Path.Combine(ProjectPath, ".bdtool", "state", "activity");

    public string ArtifactsRoot => System.IO.Path.Combine(ProjectPath, ".bdtool", "artifacts");

    /// <summary>返回项目内路径的绝对形式；允许调用方传入带前导分隔符的相对路径。</summary>
    public string Resolve(string relativePath) =>
        System.IO.Path.Combine(ProjectPath, relativePath.TrimStart('\\', '/'));

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
