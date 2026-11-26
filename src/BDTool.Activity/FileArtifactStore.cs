using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts.Artifacts;
using BDTool.Core.IO;
using BDTool.Core.Projects;

namespace BDTool.Activity;

/// <summary>
/// Artifact 的本地文件实现。写入使用同目录临时文件后原子重命名，
/// 读取只接受落在本项目 artifacts 根目录内的引用，越界路径一律拒绝。
/// </summary>
public sealed class FileArtifactStore : IArtifactStore
{
    /// <summary>无法从扩展名推断内容类型时使用的默认值。</summary>
    public const string DefaultContentType = "application/octet-stream";

    /// <summary>同名文件冲突时最多尝试的候选数量。</summary>
    public const int MaxNameAttempts = 1000;

    private readonly string _projectPath;
    private readonly string _artifactsRoot;

    /// <summary>使用项目根创建存储；产物根固定为 &lt;project&gt;/.bdtool/artifacts。</summary>
    public FileArtifactStore(string projectPath)
    {
        _projectPath = ProjectLayout.ResolveProjectRoot(projectPath);
        _artifactsRoot = Path.GetFullPath(ProjectLayout.GetArtifactsRoot(_projectPath));
    }

    /// <summary>
    /// 写入一个 artifact。文件名为候选名，冲突时在词干后追加序号，
    /// 因此并发写同名文件不会互相覆盖。内容 SHA-256 使用小写十六进制。
    /// </summary>
    public async Task<ArtifactReference> WriteAsync(ArtifactWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fileName = NormalizeFileName(request.FileName);
        var content = request.Content ?? Array.Empty<byte>();
        var path = await WriteUniqueAsync(fileName, content, cancellationToken).ConfigureAwait(false);
        var relativePath = ToRelativePath(path);

        return new ArtifactReference
        {
            ArtifactId = CreateArtifactId(relativePath),
            FileName = Path.GetFileName(path),
            RelativePath = relativePath,
            ContentType = NormalizeContentType(request.ContentType, path),
            SizeBytes = content.LongLength,
            Sha256 = ComputeSha256(content)
        };
    }

    /// <summary>读取 artifact 并按磁盘内容重算大小与哈希，不信任调用方传入的旧值。</summary>
    public async Task<ArtifactContent> ReadAsync(ArtifactReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var path = ResolvePath(reference);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到 Artifact：" + reference.RelativePath + "。", path);
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var relativePath = ToRelativePath(path);

        return new ArtifactContent
        {
            Bytes = bytes,
            Reference = new ArtifactReference
            {
                ArtifactId = string.IsNullOrWhiteSpace(reference.ArtifactId)
                    ? CreateArtifactId(relativePath)
                    : reference.ArtifactId,
                FileName = Path.GetFileName(path),
                RelativePath = relativePath,
                ContentType = NormalizeContentType(reference.ContentType, path),
                SizeBytes = bytes.LongLength,
                Sha256 = ComputeSha256(bytes)
            }
        };
    }

    /// <summary>判断引用是否存在；引用越出 artifacts 根目录时抛出 <see cref="ArgumentException"/>。</summary>
    public Task<bool> ExistsAsync(ArtifactReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var path = ResolvePath(reference);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(path));
    }

    private async Task<string> WriteUniqueAsync(
        string fileName,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var attempt = 0; attempt < MaxNameAttempts; attempt++)
        {
            var candidateName = attempt == 0 ? fileName : stem + "-" + (attempt + 1) + extension;
            var candidate = Path.Combine(_artifactsRoot, candidateName);
            if (await AtomicFile.WriteAsync(candidate, content, overwrite: false, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        throw new IOException("无法为 artifact 分配唯一文件名：" + fileName);
    }

    /// <summary>
    /// 解析引用到 artifacts 根内的绝对路径。同时接受“相对项目根”和
    /// “相对 artifacts 根”两种写法，但最终必须落在 artifacts 根内。
    /// </summary>
    private string ResolvePath(ArtifactReference reference)
    {
        var relative = !string.IsNullOrWhiteSpace(reference.RelativePath)
            ? reference.RelativePath
            : reference.FileName;

        if (string.IsNullOrWhiteSpace(relative))
        {
            throw new ArgumentException("Artifact 引用必须包含 RelativePath 或 FileName。", nameof(reference));
        }

        var normalized = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            throw new ArgumentException("Artifact 引用必须是相对于项目根的路径。", nameof(reference));
        }

        foreach (var basePath in new[] { _projectPath, _artifactsRoot })
        {
            var fullPath = Path.GetFullPath(Path.Combine(basePath, normalized));
            if (IsInsideArtifactsRoot(fullPath))
            {
                return fullPath;
            }
        }

        throw new ArgumentException("Artifact 路径越出 artifacts 根目录。", nameof(reference));
    }

    private bool IsInsideArtifactsRoot(string fullPath)
    {
        var rootWithSeparator = _artifactsRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _artifactsRoot
            : _artifactsRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 校验并规范化文件名：拒绝路径分隔符、根路径、非法字符和 Windows 保留设备名，
    /// 保证最终文件名不能把写入带出 artifacts 根目录。
    /// </summary>
    private static string NormalizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Artifact 文件名不能为空。", nameof(fileName));
        }

        var normalized = fileName.Trim();
        if (normalized.Length > 200)
        {
            throw new ArgumentException("Artifact 文件名不能超过 200 个字符。", nameof(fileName));
        }

        if (Path.IsPathRooted(normalized))
        {
            throw new ArgumentException("Artifact 文件名不能是绝对路径。", nameof(fileName));
        }

        if (normalized.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            throw new ArgumentException("Artifact 文件名不能包含路径分隔符。", nameof(fileName));
        }

        if (normalized is "." or "..")
        {
            throw new ArgumentException("Artifact 文件名不能是相对目录标记。", nameof(fileName));
        }

        if (normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Artifact 文件名包含非法字符。", nameof(fileName));
        }

        if (normalized.EndsWith('.') || normalized.EndsWith(' '))
        {
            throw new ArgumentException("Artifact 文件名不能以点或空格结尾。", nameof(fileName));
        }

        if (IsReservedDeviceName(Path.GetFileNameWithoutExtension(normalized)))
        {
            throw new ArgumentException("Artifact 文件名不能使用 Windows 保留设备名。", nameof(fileName));
        }

        return normalized;
    }

    private static bool IsReservedDeviceName(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return true;
        }

        var upper = stem.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL")
        {
            return true;
        }

        if (upper.Length == 4
            && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)))
        {
            return upper[3] is >= '1' and <= '9';
        }

        return false;
    }

    private static string NormalizeContentType(string? contentType, string path)
    {
        if (string.IsNullOrWhiteSpace(contentType)
            || string.Equals(contentType, DefaultContentType, StringComparison.OrdinalIgnoreCase))
        {
            return GuessContentType(path);
        }

        return contentType;
    }

    private static string GuessContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".txt" or ".log" => "text/plain",
            ".md" => "text/markdown",
            ".csv" => "text/csv",
            ".xml" => "application/xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".zip" => "application/zip",
            _ => DefaultContentType
        };

    private string ToRelativePath(string fullPath) =>
        Path.GetRelativePath(_projectPath, fullPath).Replace('\\', '/');

    private static string CreateArtifactId(string relativePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(relativePath));
        return "art-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string ComputeSha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
