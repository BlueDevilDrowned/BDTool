using System;
using System.IO;

namespace BDTool.Tests.Support;

/// <summary>
/// 定位解决方案根目录。测试必须读取真实工程文件，因此从测试输出目录
/// 向上查找 BDTool.sln，而不是依赖当前工作目录。
/// </summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string ProjectFile(string projectName)
    {
        var directory = projectName.StartsWith("BDTool.Tests", StringComparison.Ordinal)
            ? Path.Combine(Root, "tests", projectName)
            : Path.Combine(Root, "src", projectName);
        return Path.Combine(directory, projectName + ".csproj");
    }

    public static string CatalogFile => Path.Combine(Root, "catalog", "operations.json");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BDTool.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未能从测试输出目录找到 BDTool.sln。");
    }
}
