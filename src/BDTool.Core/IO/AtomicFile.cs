using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BDTool.Core.IO;

/// <summary>
/// 同目录临时文件加原子替换的共享写入实现。Activity、Artifact 与后续 Workspace 写操作
/// 都使用这一份实现，避免不同模块在持久化细节上产生漂移。
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding s_strictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>以覆盖方式写入原始字节。</summary>
    public static Task<bool> WriteAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        return WriteAsync(destinationPath, content, overwrite: true, cancellationToken);
    }

    /// <summary>
    /// 以 UTF-8 无 BOM 写入文本。编码器拒绝孤立代理项，避免悄悄写入非法字节。
    /// </summary>
    public static Task<bool> WriteUtf8Async(
        string destinationPath,
        string content,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (content == null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        return WriteAsync(
            destinationPath,
            s_strictUtf8.GetBytes(content),
            overwrite,
            cancellationToken);
    }

    /// <summary>
    /// 把内容写入目标路径。overwrite=true 时整文件替换已有内容；
    /// overwrite=false 时若目标已存在则返回 false，用于 artifact 的唯一命名。
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

        cancellationToken.ThrowIfCancellationRequested();
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

            // 提交点之前响应取消，finally 会删除临时文件；一旦 Move 成功，
            // 本次写入已提交，不再因随后取消向调用方报告失败。
            cancellationToken.ThrowIfCancellationRequested();

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
