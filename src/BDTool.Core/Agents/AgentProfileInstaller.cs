using BDTool.Contracts.Agents;
using BDTool.Core.IO;
using BDTool.Core.Workspace;

namespace BDTool.Core.Agents;

/// <summary>
/// 把 BDTool 固定 Agent Profile 安装到项目级 &lt;project&gt;/.codex/agents。
/// 只更新带 BDTool 管理标记的文件；预检发现任何未受管同名文件时不写入任何 Profile。
/// </summary>
public sealed class AgentProfileInstaller
{
    public const string ManagedMarker = "# bdtool-managed: agent-profile-v1";
    public const string ProjectScope = "project";

    private readonly string _sourceRoot;

    public AgentProfileInstaller(string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            throw new ArgumentException("Agent Profile 源目录不能为空。", nameof(sourceRoot));
        }

        _sourceRoot = PathPolicy.NormalizeProjectRoot(sourceRoot);
    }

    public AgentProfileInstallResult Install(string projectRoot)
    {
        var root = PathPolicy.NormalizeProjectRoot(projectRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("项目目录不存在：" + root);
        }

        if (!Directory.Exists(_sourceRoot))
        {
            throw new DirectoryNotFoundException("Agent Profile 源目录不存在：" + _sourceRoot);
        }

        var agentsRoot = Path.Combine(root, ".codex", "agents");
        PathPolicy.EnsureNoReparsePoint(root, Path.Combine(root, ".codex"));
        PathPolicy.EnsureNoReparsePoint(root, agentsRoot);

        var sources = Directory.EnumerateFiles(
                _sourceRoot,
                "*.toml",
                SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(path => new SourceProfile(
                Path.GetFileNameWithoutExtension(path),
                Path.GetFileName(path),
                path,
                File.ReadAllText(path)))
            .ToArray();
        if (sources.Length == 0)
        {
            throw new InvalidOperationException("Agent Profile 源目录没有 .toml 文件。");
        }

        var result = new AgentProfileInstallResult
        {
            Scope = ProjectScope,
            ProjectPath = root,
            AgentsRoot = agentsRoot
        };
        foreach (var source in sources)
        {
            ValidateSource(source);
            var destination = Path.Combine(agentsRoot, source.FileName);
            if (Directory.Exists(destination))
            {
                result.Conflicts.Add(new AgentProfileInstallConflict
                {
                    Name = source.Name,
                    DestinationPath = destination,
                    Reason = "目标路径是目录。"
                });
                continue;
            }

            if (File.Exists(destination))
            {
                var existing = File.ReadAllText(destination);
                if (!existing.StartsWith(ManagedMarker, StringComparison.Ordinal))
                {
                    result.Conflicts.Add(new AgentProfileInstallConflict
                    {
                        Name = source.Name,
                        DestinationPath = destination,
                        Reason = "同名 Agent Profile 未受 BDTool 管理。"
                    });
                }
            }
        }

        if (result.Conflicts.Count > 0)
        {
            return result;
        }

        Directory.CreateDirectory(agentsRoot);
        PathPolicy.EnsureNoReparsePoint(root, agentsRoot);
        foreach (var source in sources)
        {
            var destination = Path.Combine(agentsRoot, source.FileName);
            PathPolicy.EnsureNoReparsePoint(root, destination);
            var existed = File.Exists(destination);
            var updated = !existed
                || !string.Equals(
                    File.ReadAllText(destination),
                    source.Content,
                    StringComparison.Ordinal);
            if (updated)
            {
                AtomicFile.WriteUtf8Async(
                        destination,
                        source.Content,
                        overwrite: true,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }

            result.Profiles.Add(new AgentProfileInstallItem
            {
                Name = source.Name,
                DestinationPath = destination,
                Updated = updated,
                Managed = true
            });
        }

        return result;
    }

    private static void ValidateSource(SourceProfile source)
    {
        if (!source.Name.StartsWith("bdtool-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Agent Profile 文件名必须以 bdtool- 开头：" + source.FileName);
        }

        if (!source.Content.StartsWith(ManagedMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Agent Profile 缺少管理标记：" + source.FileName);
        }

        if (source.Content.Contains("\0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Agent Profile 不能包含 NUL。");
        }
    }

    private sealed record SourceProfile(
        string Name,
        string FileName,
        string SourcePath,
        string Content);
}
