using System.Security.Cryptography;
using System.Text;
using BDTool.Core.Workspace;

namespace BDTool.Provider.Local.Internal;

/// <summary>
/// M1 文本边界：严格 UTF-8、可选 BOM、拒绝 NUL，并对原始磁盘字节计算指纹。
/// 这里不处理换行，调用方拿到的 Content 与磁盘上的 CRLF/LF 完全一致。
/// </summary>
internal static class WorkspaceTextCodec
{
    /// <summary>MemoryStream 一次性预分配上限；超出部分依赖自动扩容，避免大文件瞬间占满内存。</summary>
    private const int MaxPreallocationBytes = 1024 * 1024;

    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryDecode(
        ReadOnlySpan<byte> rawBytes,
        out string content,
        out string failureReason)
    {
        var contentBytes = StripBom(rawBytes);

        try
        {
            content = s_strictUtf8.GetString(contentBytes);
        }
        catch (DecoderFallbackException)
        {
            content = string.Empty;
            failureReason = "invalid-utf8";
            return false;
        }

        if (content.IndexOf('\0') >= 0)
        {
            content = string.Empty;
            failureReason = "nul-character";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    public static bool TryEncode(
        string? content,
        out byte[] bytes,
        out string failureReason)
    {
        bytes = Array.Empty<byte>();
        if (content is null)
        {
            failureReason = "content-null";
            return false;
        }

        if (content.IndexOf('\0') >= 0)
        {
            failureReason = "nul-character";
            return false;
        }

        try
        {
            bytes = s_strictUtf8.GetBytes(content);
        }
        catch (EncoderFallbackException)
        {
            // 孤立代理项无法表示成合法 UTF-8；必须拒绝而不是写入替换字符。
            failureReason = "invalid-utf16";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    public static long GetUtf8ByteCount(string content)
    {
        return s_strictUtf8.GetByteCount(content);
    }

    public static string ComputeSha256(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// 读取不超过上限的原始字节。返回 Exceeded 而不是截断，调用方据此分别执行“拒绝读取”
    /// 或“搜索计数”。流式循环还能覆盖文件在 Length 检查之后继续增长的竞争情形。
    /// </summary>
    public static async Task<WorkspaceLimitedRead> ReadLimitedAsync(
        string path,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        // 打开句柄后再看一次 reparse 标志，缩小路径检查与读取之间的修复窗口。
        if (PathPolicy.IsReparsePoint(path))
        {
            throw new PathPolicyException(PathPolicyViolation.ReparsePoint, "路径包含 reparse point。");
        }

        if (stream.Length > maxBytes)
        {
            return WorkspaceLimitedRead.ExceededValue;
        }

        // 容量参数是 int：上限可能大于 int.MaxValue，磁盘文件也可能超过 2 GiB，
        // 必须收敛后再强转，否则 (int) 会溢出成负值并抛 ArgumentOutOfRangeException。
        // 同时限制一次性预分配：直接按 Length 申请会让超大文件立刻尝试分配 2 GiB。
        var capacity = (int)Math.Min(Math.Min(stream.Length, maxBytes), MaxPreallocationBytes);
        using var buffer = new MemoryStream(capacity);
        var chunk = new byte[81920];
        long totalBytes = 0;

        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalBytes += read;
            if (totalBytes > maxBytes)
            {
                return WorkspaceLimitedRead.ExceededValue;
            }

            buffer.Write(chunk, 0, read);
        }

        return new WorkspaceLimitedRead(buffer.ToArray(), exceeded: false);
    }

    private static ReadOnlySpan<byte> StripBom(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;
    }
}

/// <summary>受限读取结果；Exceeded 时不携带部分内容，避免调用方误把截断结果当完整文件。</summary>
internal readonly struct WorkspaceLimitedRead
{
    public static WorkspaceLimitedRead ExceededValue { get; } = new(Array.Empty<byte>(), exceeded: true);

    public WorkspaceLimitedRead(byte[] bytes, bool exceeded)
    {
        Bytes = bytes;
        Exceeded = exceeded;
    }

    public byte[] Bytes { get; }

    public bool Exceeded { get; }
}
