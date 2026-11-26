using System;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Core.Projects;
using BDTool.Operations.Diagnostics;

namespace BDTool.Application.Diagnostics;

/// <summary>
/// 通过真实文件系统探测 doctor 所需的本地状态。探测只创建 .bdtool 状态目录并写入
/// 一个短生命周期探针文件，不创建项目目录、不访问 Unity，也不读取任何业务内容。
/// </summary>
public sealed class FileSystemDoctorEnvironmentProbe : IDoctorEnvironmentProbe
{
    private const string ProbeFileNamePrefix = ".bdtool-write-probe-";

    /// <inheritdoc />
    public async Task<DoctorEnvironmentSnapshot> ProbeAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        // 端口来自公共接口，调用方仍可能传入 null；先归一为空串，
        // 后续路径解析与快照都不会携带可空值。
        projectPath ??= string.Empty;
        var snapshot = new DoctorEnvironmentSnapshot
        {
            ProjectPath = projectPath
        };

        string projectRoot;
        try
        {
            projectRoot = ProjectLayout.ResolveProjectRoot(projectPath);
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            snapshot.ProjectExists = false;
            snapshot.ProjectDetail = "项目路径无效：" + exception.GetType().Name;
            snapshot.StateRootDetail = "项目路径无效，未创建状态目录。";
            snapshot.ActivityRootDetail = "项目路径无效，未创建 Activity 目录。";
            return snapshot;
        }

        snapshot.ProjectPath = projectRoot;
        snapshot.StateRoot = ProjectLayout.GetStateRoot(projectRoot);
        snapshot.ActivityRoot = ProjectLayout.GetActivityRoot(projectRoot);
        snapshot.ProjectExists = Directory.Exists(projectRoot);

        if (!snapshot.ProjectExists)
        {
            // 不替用户创建项目目录：项目缺失本身就是 doctor 的失败项。
            snapshot.ProjectDetail = "项目目录不存在。";
            snapshot.StateRootDetail = "项目不存在，未创建状态目录。";
            snapshot.ActivityRootDetail = "项目不存在，未创建 Activity 目录。";
            return snapshot;
        }

        snapshot.ProjectDetail = "项目目录存在。";

        var stateResult = await ProbeWritableAsync(snapshot.StateRoot, cancellationToken).ConfigureAwait(false);
        snapshot.StateRootWritable = stateResult.Writable;
        snapshot.StateRootDetail = stateResult.Detail;

        var activityResult = await ProbeWritableAsync(snapshot.ActivityRoot, cancellationToken).ConfigureAwait(false);
        snapshot.ActivityRootWritable = activityResult.Writable;
        snapshot.ActivityRootDetail = activityResult.Detail;

        return snapshot;
    }

    /// <summary>
    /// 创建目录、写入并删除探针文件，确认目录在探测当下确实可写。
    /// 取消请求向上传播，其余文件系统错误转成可读诊断，避免 doctor 自身抛异常。
    /// </summary>
    private static async Task<(bool Writable, string Detail)> ProbeWritableAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var probePath = Path.Combine(
            directory,
            ProbeFileNamePrefix + Guid.NewGuid().ToString("n") + ".tmp");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(probePath, "bdtool", cancellationToken).ConfigureAwait(false);
            return (true, "目录可创建并可写。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            return (false, "不可写：" + exception.GetType().Name + ": " + Trim(exception.Message));
        }
        finally
        {
            TryDelete(probePath);
        }
    }

    private static bool IsPathFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or ArgumentException;

    private static string Trim(string message)
    {
        const int maxLength = 240;
        if (string.IsNullOrEmpty(message) || message.Length <= maxLength)
        {
            return message ?? string.Empty;
        }

        return message.Substring(0, maxLength) + "…";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 探针清理失败不影响判定结果，残留文件由后续维护清理。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
