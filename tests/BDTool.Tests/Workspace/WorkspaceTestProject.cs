using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BDTool.Contracts.Workspace;
using BDTool.Provider.Local;

namespace BDTool.Tests.Workspace;

/// <summary>
/// Workspace 测试使用的临时项目根。所有路径都从实例根派生，避免测试之间共享状态；
/// 清理时先删除 reparse point 再递归删除普通目录，防止链接把删除动作带到真实目标。
/// </summary>
internal sealed class WorkspaceTestProject : IDisposable
{
    public WorkspaceTestProject()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "bdtool-workspace-tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public LocalWorkspaceKernel CreateKernel(Action<LocalWorkspaceOptions>? configure = null)
    {
        var options = new LocalWorkspaceOptions();
        configure?.Invoke(options);
        return new LocalWorkspaceKernel(Root, options);
    }

    public WorkspaceReadRequest ReadRequest(string relativePath)
    {
        return new WorkspaceReadRequest
        {
            ProjectPath = Root,
            RelativePath = relativePath
        };
    }

    public WorkspaceSearchRequest SearchRequest(
        string query,
        string glob = "**/*",
        int maxResults = 200)
    {
        return new WorkspaceSearchRequest
        {
            ProjectPath = Root,
            Query = query,
            Glob = glob,
            MaxResults = maxResults
        };
    }

    public WorkspaceFingerprintRequest FingerprintRequest(params string[] relativePaths)
    {
        var request = new WorkspaceFingerprintRequest { ProjectPath = Root };
        request.RelativePaths.AddRange(relativePaths);
        return request;
    }

    public WorkspaceChangeRequest ChangeRequest(
        string relativePath,
        string content,
        string? expectedSha256 = null,
        bool createIfMissing = false,
        bool dryRun = false)
    {
        return new WorkspaceChangeRequest
        {
            ProjectPath = Root,
            RelativePath = relativePath,
            Content = content,
            ExpectedSha256 = expectedSha256,
            CreateIfMissing = createIfMissing,
            DryRun = dryRun
        };
    }

    public string Resolve(string relativePath)
    {
        return Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string WriteBytes(string relativePath, byte[] bytes)
    {
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public string WriteText(string relativePath, string content, bool withBom = false)
    {
        var body = Encoding.UTF8.GetBytes(content);
        var bytes = withBom
            ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray()
            : body;
        return WriteBytes(relativePath, bytes);
    }

    public string ReadText(string relativePath)
    {
        return File.ReadAllText(Resolve(relativePath), Encoding.UTF8);
    }

    public byte[] ReadBytes(string relativePath)
    {
        return File.ReadAllBytes(Resolve(relativePath));
    }

    public string Hash(string relativePath)
    {
        return Sha256(ReadBytes(relativePath));
    }

    public string[] TemporaryFiles()
    {
        return Directory
            .EnumerateFiles(Root, "*.tmp", SearchOption.AllDirectories)
            .ToArray();
    }

    public static string Sha256(string text)
    {
        return Sha256(Encoding.UTF8.GetBytes(text));
    }

    public static string Sha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public void Dispose()
    {
        DeleteTree(Root);
    }

    private static void DeleteTree(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // 链接只删除链接本身；不要递归进入目标目录。
                    if (entry is DirectoryInfo link)
                    {
                        link.Delete();
                    }
                    else
                    {
                        entry.Delete();
                    }

                    continue;
                }

                if (entry is DirectoryInfo child)
                {
                    DeleteTree(child.FullName);
                }
                else
                {
                    if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                    {
                        entry.Attributes &= ~FileAttributes.ReadOnly;
                    }

                    entry.Delete();
                }
            }

            Directory.Delete(directory);
        }
        catch (IOException)
        {
            // 测试清理失败不应掩盖断言结果；系统会在后续清理临时目录时回收残留。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// 符号链接创建辅助。Windows 需要开发者模式或提升权限；环境不支持时返回 false。
/// 依赖它的测试统一标注 ReparsePointFact：发现阶段用 IsSupported 探测，
/// 不具备能力时由测试框架记为“跳过”，避免出现没有断言的通过。
/// </summary>
internal static class WorkspaceReparsePoints
{
    private static readonly Lazy<bool> s_supported = new(ProbeDirectoryLinkSupport);

    /// <summary>当前环境是否能创建至少一种目录 reparse point（符号链接或 junction）。</summary>
    public static bool IsSupported => s_supported.Value;

    /// <summary>
    /// 在临时目录做一次真实的创建/清理探测，供发现阶段决定是否跳过相关测试。
    /// 设置环境变量 BDTOOL_TEST_FORCE_NO_REPARSE=1 可强制判定为不支持，
    /// 用于验证跳过路径本身确实被测试框架识别。
    /// </summary>
    private static bool ProbeDirectoryLinkSupport()
    {
        if (Environment.GetEnvironmentVariable("BDTOOL_TEST_FORCE_NO_REPARSE") == "1")
        {
            return false;
        }

        var root = Path.Combine(Path.GetTempPath(), "bdtool-reparse-probe-" + Guid.NewGuid().ToString("n"));
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        try
        {
            Directory.CreateDirectory(target);
            return TryCreateDirectoryLink(link, target);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
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

    public static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }

        // 普通用户没有符号链接权限时，Windows 仍允许创建 junction。junction 同样带
        // ReparsePoint 属性，因此可以覆盖目录遍历与项目根的 reparse 分支；文件链接
        // 没有等价的无权限替代，相关用例仍按环境条件跳过。
        return OperatingSystem.IsWindows() && TryCreateJunction(linkPath, targetPath);
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.HasExited
                && process.ExitCode == 0
                && Directory.Exists(linkPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
