namespace BDTool.Tests.Formation;

/// <summary>M7 用例专用项目。状态固定创建在 E 盘测试区，Dispose 前校验路径边界。</summary>
internal sealed class FormationTestProject : IDisposable
{
    private const string TestArea = @"E:\AI\BDTools\M7TestArea";

    public FormationTestProject()
    {
        ProjectPath = Path.Combine(TestArea, "cases", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(ProjectPath);
    }

    public string ProjectPath { get; }

    public void Dispose()
    {
        var fullPath = Path.GetFullPath(ProjectPath);
        var testArea = Path.TrimEndingDirectorySeparator(Path.GetFullPath(TestArea));
        if (!fullPath.StartsWith(
                testArea + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝清理测试区之外的目录。");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
