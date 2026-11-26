using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BDTool.Activity;

/// <summary>
/// 同目录临时文件 + 原子重命名的写入工具。Activity 与 Artifact 都通过这里落盘，
/// 保证任何时刻磁盘上看到的都是完整文件，不会出现写了一半的 JSON 或产物。
/// </summary>
internal static class AtomicFile
{
    /// <summary>
    /// 把内容写入目标路径。overwrite=true 时整文件替换已有内容，
    /// overwrite=false 时若目标已存在则返回 false（用于 artifact 的唯一命名）。
    /// </summary>
    /// <returns>是否成功写入目标路径。</returns>
    public static async Task<bool> WriteAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("目标路径不能为空。", nameof(destinationPath));
        }

        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("目标路径缺少目录部分。", nameof(destinationPath));
        Directory.CreateDirectory(directory);

        // 临时文件与目标同目录，确保重命名在同一卷内完成；扩展名不是 .json，
        // 查询枚举不会把未完成的写入当成 Activity 记录。
        var tempPath = Path.Combine(
            directory,
            "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("n") + ".tmp");

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(tempPath, destinationPath, overwrite);
            }
            catch (IOException) when (!overwrite && File.Exists(destinationPath))
            {
                // 并发写入同名 artifact 时由调用方换下一个候选文件名。
                return false;
            }

            return true;
        }
        finally
        {
            TryDelete(tempPath);
        }
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
            // 临时文件清理失败不影响已完成的写入，留给后续维护处理。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
