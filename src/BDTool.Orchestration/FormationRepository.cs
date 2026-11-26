using System.Globalization;
using System.Text;
using System.Text.Json;
using BDTool.Contracts.Formation;
using BDTool.Core.IO;
using BDTool.Core.Projects;
using BDTool.Core.Workspace;

namespace BDTool.Orchestration;

/// <summary>
/// Formation 文件仓储。每个 formation 使用独立 JSON 文件与跨进程独占锁；
/// 所有提交均通过 AtomicFile 替换，读取永远看到旧版本或完整新版本。
/// </summary>
internal sealed class FormationRepository
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _rootPath;

    public FormationRepository(string projectRoot)
    {
        ProjectRoot = PathPolicy.NormalizeProjectRoot(projectRoot);
        _rootPath = ProjectLayout.GetFormationsRoot(ProjectRoot);
    }

    public string ProjectRoot { get; }

    public async Task<FormationData?> ReadAsync(
        string formationId,
        CancellationToken cancellationToken)
    {
        var path = GetFormationPath(formationId);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await ReadSharedTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Deserialize(path, json);
    }

    /// <summary>
    /// 按枚举到的文件路径读取。列表查询与 mutation 并发时仍允许原子替换删除旧句柄，
    /// 避免普通 ReadAllText 的 FileShare.Read 阻塞 Windows 文件替换。
    /// </summary>
    public async Task<FormationData> ReadByPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var json = await ReadSharedTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Deserialize(path, json);
    }

    public async Task WriteAsync(
        FormationData formation,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(formation, s_jsonOptions);
        var path = GetFormationPath(formation.Id);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await AtomicFile.WriteUtf8Async(
                        path,
                        json,
                        overwrite: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
                when (attempt < 20
                      && exception is IOException or UnauthorizedAccessException)
            {
                // 无锁读取允许 AtomicFile 删除旧句柄，但 Windows 仍可能在替换瞬间
                // 短暂拒绝访问；只在 Formation 写入入口做有界重试，最终失败原样抛出。
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public IReadOnlyList<string> ListFiles()
    {
        if (!Directory.Exists(_rootPath))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(_rootPath, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<FileStream> AcquireLockAsync(
        string formationId,
        CancellationToken cancellationToken)
    {
        PathPolicy.EnsureNoReparsePoint(
            ProjectRoot,
            Path.Combine(ProjectRoot, ProjectLayout.GetStateRoot(ProjectRoot)));
        Directory.CreateDirectory(_rootPath);
        var lockPath = Path.Combine(_rootPath, formationId + ".lock");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PathPolicy.EnsureNoReparsePoint(ProjectRoot, _rootPath);
                PathPolicy.EnsureNoReparsePoint(ProjectRoot, lockPath);
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new FormationServiceException(
                    BDTool.Contracts.Errors.ErrorCodes.PreconditionFailed,
                    "Formation 状态文件正被其他进程占用。",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["lockPath"] = lockPath,
                        ["waitedMilliseconds"] = "5000"
                    },
                    retryable: true);
            }
        }
    }

    public FormationData Deserialize(string path, string json)
    {
        try
        {
            var formation = JsonSerializer.Deserialize<FormationData>(json, s_jsonOptions)
                ?? throw new JsonException("Formation 文件内容为空。");
            if (formation.SchemaVersion != 1)
            {
                throw new FormationServiceException(
                    "FORMATION_STATE_UNSUPPORTED",
                    "Formation 状态文件版本不受支持。",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["filePath"] = path,
                        ["schemaVersion"] = formation.SchemaVersion.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                    });
            }

            return formation;
        }
        catch (JsonException exception)
        {
            throw new FormationServiceException(
                "FORMATION_STATE_CORRUPT",
                "Formation 文件损坏：" + Path.GetFileName(path),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["filePath"] = path,
                    ["reason"] = exception.Message
                });
        }
    }

    public string GetFormationPath(string formationId) =>
        Path.Combine(_rootPath, formationId + ".json");

    public FormationData CloneForOutput(FormationData formation, bool includeReceipts)
    {
        var clone = JsonSerializer.Deserialize<FormationData>(
            JsonSerializer.Serialize(formation, s_jsonOptions),
            s_jsonOptions)
            ?? throw new JsonException("Formation projection failed.");
        if (!includeReceipts)
        {
            clone.RequestReceipts.Clear();
        }

        return clone;
    }

    public static string NextId(string prefix) =>
        prefix + "-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private static async Task<string> ReadSharedTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
