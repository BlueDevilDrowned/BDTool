using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using BDTool.Contracts;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Providers;
using BDTool.Contracts.Workspace;
using BDTool.Core.IO;
using BDTool.Core.Workspace;
using BDTool.Provider.Local.Internal;
using BDTool.Provider.Local.Search;

namespace BDTool.Provider.Local;

/// <summary>
/// 真实本地文件系统上的 Workspace Provider。实例绑定一个项目根，所有请求路径再次经过
/// PathPolicy 校验；读取/搜索/指纹/写入共享同一套路径、编码和原子写规则。
/// </summary>
public sealed class LocalWorkspaceKernel : IWorkspaceKernel
{
    private const string ProviderId = "local";
    private const string ProviderDisplayName = "Local Workspace";
    private const string ImplementationVersion = "0.1.0";

    private readonly string _projectRoot;
    private readonly LocalWorkspaceOptions _options;

    public LocalWorkspaceKernel(string projectPath, LocalWorkspaceOptions? options = null)
    {
        var effectiveOptions = options ?? new LocalWorkspaceOptions();
        ValidateOptions(effectiveOptions);

        var effectiveProjectPath = string.IsNullOrWhiteSpace(effectiveOptions.ProjectPath)
            ? projectPath
            : effectiveOptions.ProjectPath;

        try
        {
            _projectRoot = PathPolicy.NormalizeProjectRoot(effectiveProjectPath);
        }
        catch (PathPolicyException exception)
        {
            throw WorkspaceErrors.FromPathPolicy(exception, effectiveProjectPath, relativePath: null);
        }

        // 快照限制，避免调用方在 Kernel 运行期间修改 options 改变已经暴露的 Provider 契约。
        _options = new LocalWorkspaceOptions
        {
            ProjectPath = _projectRoot,
            MaxReadBytes = effectiveOptions.MaxReadBytes,
            MaxSearchFileBytes = effectiveOptions.MaxSearchFileBytes,
            MaxSearchResults = effectiveOptions.MaxSearchResults,
            MaxApplyBytes = effectiveOptions.MaxApplyBytes
        };
    }

    public Task<ProviderDescriptor> DescribeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var descriptor = new ProviderDescriptor
        {
            Id = ProviderId,
            DisplayName = ProviderDisplayName,
            Kind = ProviderKinds.Workspace,
            ContractVersion = BDToolContract.Version,
            ImplementationVersion = ImplementationVersion,
            UnityRange = string.Empty,
            Capabilities = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [CapabilityIds.WorkspaceRead] = true,
                [CapabilityIds.WorkspaceWrite] = true,
                [CapabilityIds.WorkspaceSearch] = true,
                [CapabilityIds.WorkspaceFingerprint] = true
            },
            Limits = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["maxReadBytes"] = _options.MaxReadBytes.ToString(CultureInfo.InvariantCulture),
                ["maxSearchFileBytes"] = _options.MaxSearchFileBytes.ToString(CultureInfo.InvariantCulture),
                ["maxSearchResults"] = _options.MaxSearchResults.ToString(CultureInfo.InvariantCulture),
                ["maxApplyBytes"] = _options.MaxApplyBytes.ToString(CultureInfo.InvariantCulture)
            }
        };

        return Task.FromResult(descriptor);
    }

    public async Task<WorkspaceReadResult> ReadAsync(
        WorkspaceReadRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw WorkspaceErrors.Invalid("workspace.read 请求不能为空。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var projectRoot = ResolveProjectRoot(request.ProjectPath);
        EnsureProjectDirectory(projectRoot);
        var location = ResolvePath(projectRoot, request.RelativePath);

        if (Directory.Exists(location.AbsolutePath))
        {
            throw WorkspaceErrors.Invalid(
                "workspace.read 的目标是目录，必须指定文件。",
                ("relativePath", location.RelativePath));
        }

        if (!File.Exists(location.AbsolutePath))
        {
            throw WorkspaceErrors.NotFound(
                "workspace.read 的目标文件不存在。",
                ("relativePath", location.RelativePath));
        }

        try
        {
            // 文件系统状态可能在首次校验后变化；真正打开前再检查整条路径。
            PathPolicy.EnsureNoReparsePoint(projectRoot, location.AbsolutePath);
            var limitedRead = await WorkspaceTextCodec.ReadLimitedAsync(
                location.AbsolutePath,
                _options.MaxReadBytes,
                cancellationToken).ConfigureAwait(false);

            if (limitedRead.Exceeded)
            {
                throw WorkspaceErrors.Invalid(
                    "文件超过 workspace.read 的读取上限。",
                    ("relativePath", location.RelativePath),
                    ("maxReadBytes", _options.MaxReadBytes.ToString(CultureInfo.InvariantCulture)));
            }

            if (!WorkspaceTextCodec.TryDecode(
                limitedRead.Bytes,
                out var content,
                out var failureReason))
            {
                throw WorkspaceErrors.Invalid(
                    "文件不是 M1 支持的合法 UTF-8 文本。",
                    ("relativePath", location.RelativePath),
                    ("reason", failureReason));
            }

            return new WorkspaceReadResult
            {
                RelativePath = location.RelativePath,
                Content = content,
                Length = WorkspaceTextCodec.GetUtf8ByteCount(content),
                Sha256 = WorkspaceTextCodec.ComputeSha256(limitedRead.Bytes)
            };
        }
        catch (PathPolicyException exception)
        {
            throw WorkspaceErrors.FromPathPolicy(exception, projectRoot, location.RelativePath);
        }
        catch (FileNotFoundException)
        {
            throw WorkspaceErrors.NotFound(
                "workspace.read 的目标文件在读取前被移除。",
                ("relativePath", location.RelativePath));
        }
        catch (DirectoryNotFoundException)
        {
            throw WorkspaceErrors.NotFound(
                "workspace.read 的目标目录在读取前被移除。",
                ("relativePath", location.RelativePath));
        }
        catch (UnauthorizedAccessException exception)
        {
            throw WorkspaceErrors.ExecutionFailed("没有权限读取目标文件。", exception);
        }
        catch (IOException exception)
        {
            throw WorkspaceErrors.ExecutionFailed("读取目标文件失败。", exception);
        }
    }

    public async Task<WorkspaceSearchResult> SearchAsync(
        WorkspaceSearchRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw WorkspaceErrors.Invalid("workspace.search 请求不能为空。");
        }

        // 空查询串在 String.Contains 下恒为真，会让每个文件的每一行都命中。
        // 在遍历前拒绝，避免产生“无界匹配”这种没有返回价值的结果。
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw WorkspaceErrors.Invalid("workspace.search 的查询串不能为空或全空白。");
        }

        if (request.MaxResults <= 0)
        {
            throw WorkspaceErrors.Invalid("workspace.search 的 MaxResults 必须大于 0。");
        }

        var glob = WorkspaceGlob.Parse(
            string.IsNullOrWhiteSpace(request.Glob) ? "**/*" : request.Glob);
        var maxResults = Math.Min(request.MaxResults, _options.MaxSearchResults);
        // 多收集一条即可判定 Truncated，再多就没有返回价值。
        var collectLimit = maxResults == int.MaxValue ? int.MaxValue : maxResults + 1;

        cancellationToken.ThrowIfCancellationRequested();
        var projectRoot = ResolveProjectRoot(request.ProjectPath);
        EnsureProjectDirectory(projectRoot);

        WorkspaceWalkResult walk;
        try
        {
            walk = WorkspaceFileWalker.Walk(projectRoot, glob, cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw WorkspaceErrors.ExecutionFailed("遍历 Workspace 时没有目录访问权限。", exception);
        }
        catch (IOException exception)
        {
            throw WorkspaceErrors.ExecutionFailed("遍历 Workspace 失败。", exception);
        }

        var matches = new List<WorkspaceSearchMatch>(Math.Min(collectLimit, 256));
        var skippedBinary = 0;
        var skippedTooLarge = 0;
        var skippedReparse = walk.SkippedReparse;

        foreach (var file in walk.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 枚举结果与真正打开之间存在窗口：文件或祖先目录可能已被替换成 reparse point。
            // 打开前复用 PathPolicy 对整条父链复检，命中即计入跳过，而不是读到项目外内容。
            try
            {
                PathPolicy.EnsureNoReparsePoint(projectRoot, file.AbsolutePath);
            }
            catch (PathPolicyException exception) when (exception.Violation == PathPolicyViolation.ReparsePoint)
            {
                skippedReparse++;
                continue;
            }

            WorkspaceLimitedRead limitedRead;
            try
            {
                limitedRead = await WorkspaceTextCodec.ReadLimitedAsync(
                    file.AbsolutePath,
                    _options.MaxSearchFileBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PathPolicyException exception) when (exception.Violation == PathPolicyViolation.ReparsePoint)
            {
                // 枚举后文件被替换成 reparse point 时同样不跟随，并计入跳过统计。
                skippedReparse++;
                continue;
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (UnauthorizedAccessException exception)
            {
                throw WorkspaceErrors.ExecutionFailed("没有权限读取搜索候选文件。", exception);
            }
            catch (IOException exception)
            {
                throw WorkspaceErrors.ExecutionFailed("读取搜索候选文件失败。", exception);
            }

            if (limitedRead.Exceeded)
            {
                skippedTooLarge++;
                continue;
            }

            if (!WorkspaceTextCodec.TryDecode(limitedRead.Bytes, out var content, out _))
            {
                skippedBinary++;
                continue;
            }

            // 达到上限后仍继续扫描文件，只为让 Skipped* 计数保持完整；匹配行最多累计到
            // collectLimit 条，把内存占用稳定限制在 maxResults+1 行。
            if (matches.Count >= collectLimit)
            {
                continue;
            }

            foreach (var match in FindLineMatches(file.RelativePath, content, request.Query))
            {
                matches.Add(match);
                if (matches.Count >= collectLimit)
                {
                    break;
                }
            }
        }

        // 遍历文件与文件内行都按契约顺序产出，收集到的前缀本身就是稳定顺序；
        // 这里再排序一次作为防御，代价被 collectLimit 限定。
        matches.Sort(CompareSearchMatches);
        var truncated = matches.Count > maxResults;
        if (truncated)
        {
            matches.RemoveRange(maxResults, matches.Count - maxResults);
        }

        return new WorkspaceSearchResult
        {
            Matches = matches,
            Truncated = truncated,
            SkippedBinary = skippedBinary,
            SkippedTooLarge = skippedTooLarge,
            SkippedSymlink = skippedReparse
        };
    }

    public async Task<WorkspaceFingerprintResult> FingerprintAsync(
        WorkspaceFingerprintRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw WorkspaceErrors.Invalid("workspace.fingerprint 请求不能为空。");
        }

        if (request.RelativePaths is null)
        {
            throw WorkspaceErrors.Invalid("workspace.fingerprint 的 RelativePaths 不能为 null。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var projectRoot = ResolveProjectRoot(request.ProjectPath);
        EnsureProjectDirectory(projectRoot);

        var result = new WorkspaceFingerprintResult();
        foreach (var relativePath in request.RelativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = ResolvePath(projectRoot, relativePath);

            if (Directory.Exists(location.AbsolutePath))
            {
                throw WorkspaceErrors.Invalid(
                    "workspace.fingerprint 的目标是目录，必须指定文件。",
                    ("relativePath", location.RelativePath));
            }

            if (!File.Exists(location.AbsolutePath))
            {
                result.Files.Add(MissingFingerprint(location.RelativePath));
                continue;
            }

            try
            {
                var fileHash = await HashFileAsync(
                    projectRoot,
                    location.AbsolutePath,
                    cancellationToken).ConfigureAwait(false);
                result.Files.Add(new WorkspaceFileFingerprint
                {
                    RelativePath = location.RelativePath,
                    Exists = true,
                    Length = fileHash.Length,
                    Sha256 = fileHash.Sha256
                });
            }
            catch (PathPolicyException exception)
            {
                throw WorkspaceErrors.FromPathPolicy(exception, projectRoot, location.RelativePath);
            }
            catch (FileNotFoundException)
            {
                // 批量指纹中单个文件在检查后消失，按缺失项返回，不拖垮整批结果。
                result.Files.Add(MissingFingerprint(location.RelativePath));
            }
            catch (DirectoryNotFoundException)
            {
                result.Files.Add(MissingFingerprint(location.RelativePath));
            }
            catch (UnauthorizedAccessException exception)
            {
                throw WorkspaceErrors.ExecutionFailed("没有权限计算文件指纹。", exception);
            }
            catch (IOException exception)
            {
                throw WorkspaceErrors.ExecutionFailed("计算文件指纹失败。", exception);
            }
        }

        return result;
    }

    public async Task<WorkspaceChangeResult> ApplyAsync(
        WorkspaceChangeRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw WorkspaceErrors.Invalid("workspace.apply 请求不能为空。");
        }

        if (!WorkspaceTextCodec.TryEncode(request.Content, out var candidateBytes, out var contentFailure))
        {
            throw WorkspaceErrors.Invalid(
                "workspace.apply 的内容不是合法 UTF-8 文本。",
                ("reason", contentFailure));
        }

        // 上限在路径解析和任何写入之前判定：超限内容不产生临时文件、不创建目录；
        // dry-run 同样拒绝，避免“计划成功、实写失败”的行为分叉。
        if (candidateBytes.LongLength > _options.MaxApplyBytes)
        {
            throw WorkspaceErrors.Invalid(
                "workspace.apply 的内容超过写入上限。",
                ("reason", "content-too-large"),
                ("contentBytes", candidateBytes.LongLength.ToString(CultureInfo.InvariantCulture)),
                ("maxApplyBytes", _options.MaxApplyBytes.ToString(CultureInfo.InvariantCulture)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var projectRoot = ResolveProjectRoot(request.ProjectPath);
        EnsureProjectDirectory(projectRoot);
        var location = ResolvePath(projectRoot, request.RelativePath);

        if (Directory.Exists(location.AbsolutePath))
        {
            throw WorkspaceErrors.Invalid(
                "workspace.apply 的目标是目录，必须指定文件。",
                ("relativePath", location.RelativePath));
        }

        var exists = File.Exists(location.AbsolutePath);
        string? previousSha256 = null;

        if (exists)
        {
            if (string.IsNullOrWhiteSpace(request.ExpectedSha256))
            {
                throw WorkspaceErrors.Invalid(
                    "覆盖已有文件必须提供 ExpectedSha256。",
                    ("relativePath", location.RelativePath));
            }

            try
            {
                var currentHash = await HashFileAsync(
                    projectRoot,
                    location.AbsolutePath,
                    cancellationToken).ConfigureAwait(false);
                previousSha256 = currentHash.Sha256;

                if (!string.Equals(
                    previousSha256,
                    request.ExpectedSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw WorkspaceErrors.PreconditionFailed(
                        "文件指纹与 ExpectedSha256 不一致。",
                        ("relativePath", location.RelativePath),
                        ("expectedSha256", request.ExpectedSha256),
                        ("actualSha256", previousSha256));
                }

                if (await FileContentEqualsAsync(
                    location.AbsolutePath,
                    candidateBytes,
                    cancellationToken).ConfigureAwait(false))
                {
                    return new WorkspaceChangeResult
                    {
                        RelativePath = location.RelativePath,
                        PreviousSha256 = previousSha256,
                        NewSha256 = previousSha256,
                        Changed = false,
                        Created = false,
                        DryRun = request.DryRun
                    };
                }
            }
            catch (WorkspaceException)
            {
                throw;
            }
            catch (PathPolicyException exception)
            {
                throw WorkspaceErrors.FromPathPolicy(exception, projectRoot, location.RelativePath);
            }
            catch (FileNotFoundException)
            {
                throw WorkspaceErrors.PreconditionFailed(
                    "目标文件在写入预检期间消失。",
                    ("relativePath", location.RelativePath));
            }
            catch (DirectoryNotFoundException)
            {
                throw WorkspaceErrors.PreconditionFailed(
                    "目标目录在写入预检期间消失。",
                    ("relativePath", location.RelativePath));
            }
            catch (UnauthorizedAccessException exception)
            {
                throw WorkspaceErrors.ExecutionFailed("没有权限计算写入前指纹。", exception);
            }
            catch (IOException exception)
            {
                throw WorkspaceErrors.ExecutionFailed("写入预检失败。", exception);
            }
        }
        else
        {
            if (!request.CreateIfMissing)
            {
                throw WorkspaceErrors.NotFound(
                    "目标文件不存在且 CreateIfMissing 为 false。",
                    ("relativePath", location.RelativePath));
            }

            // dry-run 与实写共用同一份创建前置条件，避免计划成功、提交才失败。
            EnsureParentChainIsDirectory(projectRoot, location);
        }

        var newSha256 = WorkspaceTextCodec.ComputeSha256(candidateBytes);
        var created = !exists;

        var plannedResult = new WorkspaceChangeResult
        {
            RelativePath = location.RelativePath,
            PreviousSha256 = previousSha256,
            NewSha256 = newSha256,
            Changed = true,
            Created = created,
            DryRun = request.DryRun
        };

        if (request.DryRun)
        {
            return plannedResult;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await VerifyCommitPreconditionsAsync(
                projectRoot,
                location,
                previousSha256,
                cancellationToken).ConfigureAwait(false);
            PathPolicy.EnsureNoReparsePoint(projectRoot, location.AbsolutePath);

            // AtomicFile 负责同目录临时文件、落盘、Move 替换和 finally 清理。
            // 创建分支使用非覆盖 Move：二次预检之后目标被其他进程创建时 AtomicFile
            // 返回 false，这里转成 PRECONDITION_FAILED，绝不覆盖对方刚写入的内容。
            var overwrite = previousSha256 is not null;
            var written = await AtomicFile.WriteAsync(
                location.AbsolutePath,
                candidateBytes,
                overwrite,
                cancellationToken).ConfigureAwait(false);
            if (!written)
            {
                throw WorkspaceErrors.PreconditionFailed(
                    "目标在提交时已被其他进程创建，本次创建未覆盖对方内容。",
                    ("relativePath", location.RelativePath));
            }
        }
        catch (WorkspaceException)
        {
            throw;
        }
        catch (PathPolicyException exception)
        {
            throw WorkspaceErrors.FromPathPolicy(exception, projectRoot, location.RelativePath);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw WorkspaceErrors.ExecutionFailed("没有权限写入目标文件。", exception);
        }
        catch (IOException exception)
        {
            throw WorkspaceErrors.ExecutionFailed("原子写入目标文件失败。", exception);
        }

        return plannedResult;
    }

    private static void ValidateOptions(LocalWorkspaceOptions options)
    {
        if (options.MaxReadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxReadBytes 必须大于 0。");
        }

        if (options.MaxSearchFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxSearchFileBytes 必须大于 0。");
        }

        if (options.MaxSearchResults <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxSearchResults 必须大于 0。");
        }

        if (options.MaxApplyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxApplyBytes 必须大于 0。");
        }
    }

    /// <summary>
    /// 校验目标路径的父链：从项目根开始的每个已存在段都必须是目录。
    /// Apply 依赖 Directory.CreateDirectory 补建缺失目录、依赖 Move 落到目标；
    /// 若父链某段其实是普通文件，实写会在提交阶段抛裸 IOException，而 dry-run 又发现不了。
    /// 这里把两种情况统一成结构化的 INVALID_ARGUMENT，并保证发生在任何写入之前。
    /// </summary>
    private static void EnsureParentChainIsDirectory(string projectRoot, ResolvedWorkspacePath location)
    {
        var segments = location.RelativePath.Split('/');
        var current = Path.TrimEndingDirectorySeparator(projectRoot);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (Directory.Exists(current))
            {
                continue;
            }

            if (File.Exists(current))
            {
                throw WorkspaceErrors.Invalid(
                    "目标路径的父路径段已存在且不是目录。",
                    ("relativePath", location.RelativePath),
                    ("conflictPath", string.Join("/", segments, 0, index + 1)),
                    ("reason", "parent-not-directory"));
            }

            // 缺失的目录由提交阶段创建，更深的段必然也不存在。
            return;
        }
    }

    private string ResolveProjectRoot(string? requestedProjectPath)
    {
        if (string.IsNullOrWhiteSpace(requestedProjectPath))
        {
            return _projectRoot;
        }

        string normalized;
        try
        {
            normalized = PathPolicy.NormalizeProjectRoot(requestedProjectPath);
        }
        catch (PathPolicyException exception)
        {
            throw WorkspaceErrors.FromPathPolicy(exception, requestedProjectPath, relativePath: null);
        }

        if (!string.Equals(normalized, _projectRoot, PathPolicy.PathComparison))
        {
            throw WorkspaceErrors.Invalid(
                "请求中的 ProjectPath 与 Kernel 绑定的项目不一致。",
                ("configuredProjectPath", _projectRoot),
                ("requestedProjectPath", normalized));
        }

        return normalized;
    }

    private static void EnsureProjectDirectory(string projectRoot)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(projectRoot);
        }
        catch (FileNotFoundException)
        {
            throw WorkspaceErrors.ProjectNotFound(
                "Workspace 项目目录不存在。",
                ("projectPath", projectRoot));
        }
        catch (DirectoryNotFoundException)
        {
            throw WorkspaceErrors.ProjectNotFound(
                "Workspace 项目目录不存在。",
                ("projectPath", projectRoot));
        }
        catch (UnauthorizedAccessException exception)
        {
            throw WorkspaceErrors.ExecutionFailed("没有权限访问 Workspace 项目目录。", exception);
        }
        catch (IOException exception)
        {
            throw WorkspaceErrors.ProjectNotFound(
                "无法定位 Workspace 项目目录。",
                ("projectPath", projectRoot),
                ("exception", exception.GetType().Name));
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw WorkspaceErrors.Invalid(
                "ProjectPath 指向的是文件而不是目录。",
                ("projectPath", projectRoot),
                ("reason", "not-a-directory"));
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw WorkspaceErrors.Invalid(
                "ProjectPath 不能是 reparse point。",
                ("projectPath", projectRoot),
                ("reason", "reparse-point"));
        }
    }

    private static ResolvedWorkspacePath ResolvePath(string projectRoot, string? relativePath)
    {
        try
        {
            var normalized = PathPolicy.NormalizeRelativePath(relativePath ?? string.Empty);
            var absolutePath = PathPolicy.ResolveRelativePath(projectRoot, normalized);
            PathPolicy.EnsureNoReparsePoint(projectRoot, absolutePath);
            return new ResolvedWorkspacePath(normalized, absolutePath);
        }
        catch (PathPolicyException exception)
        {
            throw WorkspaceErrors.FromPathPolicy(exception, projectRoot, relativePath);
        }
    }

    private static async Task<WorkspaceHash> HashFileAsync(
        string projectRoot,
        string absolutePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (PathPolicy.IsReparsePoint(absolutePath))
        {
            throw new PathPolicyException(PathPolicyViolation.ReparsePoint, "路径包含 reparse point。");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            length += read;
        }

        var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return new WorkspaceHash(length, sha256);
    }

    private static async Task<bool> FileContentEqualsAsync(
        string absolutePath,
        ReadOnlyMemory<byte> candidateBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length != candidateBytes.Length)
        {
            return false;
        }

        var buffer = new byte[81920];
        var offset = 0;
        while (offset < candidateBytes.Length)
        {
            var expected = Math.Min(buffer.Length, candidateBytes.Length - offset);
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, expected),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            if (!buffer.AsSpan(0, read).SequenceEqual(candidateBytes.Span.Slice(offset, read)))
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static async Task VerifyCommitPreconditionsAsync(
        string projectRoot,
        ResolvedWorkspacePath location,
        string? previousSha256,
        CancellationToken cancellationToken)
    {
        if (previousSha256 is null)
        {
            if (File.Exists(location.AbsolutePath) || Directory.Exists(location.AbsolutePath))
            {
                throw WorkspaceErrors.PreconditionFailed(
                    "目标在创建预检后被其他进程创建。",
                    ("relativePath", location.RelativePath));
            }

            return;
        }

        if (!File.Exists(location.AbsolutePath))
        {
            throw WorkspaceErrors.PreconditionFailed(
                "目标在提交前被移除。",
                ("relativePath", location.RelativePath),
                ("expectedSha256", previousSha256));
        }

        var currentHash = await HashFileAsync(
            projectRoot,
            location.AbsolutePath,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentHash.Sha256, previousSha256, StringComparison.Ordinal))
        {
            throw WorkspaceErrors.PreconditionFailed(
                "目标在提交前被其他进程修改。",
                ("relativePath", location.RelativePath),
                ("expectedSha256", previousSha256),
                ("actualSha256", currentHash.Sha256));
        }
    }

    private static IEnumerable<WorkspaceSearchMatch> FindLineMatches(
        string relativePath,
        string content,
        string query)
    {
        using var reader = new StringReader(content);
        var lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (line.Contains(query, StringComparison.Ordinal))
            {
                yield return new WorkspaceSearchMatch
                {
                    RelativePath = relativePath,
                    LineNumber = lineNumber,
                    Line = line
                };
            }
        }
    }

    private static int CompareSearchMatches(WorkspaceSearchMatch left, WorkspaceSearchMatch right)
    {
        // 先按文件系统大小写规则分组，再按 Ordinal 兜底，保证 A.txt 与 a.txt 这类
        // 仅大小写不同的路径也有确定顺序，并与 WorkspaceFileWalker 的排序口径一致。
        var comparison = StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.LineNumber.CompareTo(right.LineNumber);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Line, right.Line);
    }

    private static WorkspaceFileFingerprint MissingFingerprint(string relativePath)
    {
        return new WorkspaceFileFingerprint
        {
            RelativePath = relativePath,
            Exists = false,
            Length = 0,
            Sha256 = string.Empty
        };
    }

    private readonly record struct ResolvedWorkspacePath(string RelativePath, string AbsolutePath);

    private readonly record struct WorkspaceHash(long Length, string Sha256);
}
