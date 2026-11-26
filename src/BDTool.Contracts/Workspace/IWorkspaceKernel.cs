using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts.Providers;

namespace BDTool.Contracts.Workspace
{
    /// <summary>
    /// 本地 Workspace 的唯一入口。实现负责路径越界防护、原子写入与指纹校验。
    /// </summary>
    public interface IWorkspaceKernel
    {
        Task<ProviderDescriptor> DescribeAsync(CancellationToken cancellationToken);
        Task<WorkspaceReadResult> ReadAsync(WorkspaceReadRequest request, CancellationToken cancellationToken);
        Task<WorkspaceSearchResult> SearchAsync(WorkspaceSearchRequest request, CancellationToken cancellationToken);
        Task<WorkspaceFingerprintResult> FingerprintAsync(WorkspaceFingerprintRequest request, CancellationToken cancellationToken);
        Task<WorkspaceChangeResult> ApplyAsync(WorkspaceChangeRequest request, CancellationToken cancellationToken);
    }

    public sealed class WorkspaceReadRequest
    {
        public string ProjectPath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
    }

    public sealed class WorkspaceReadResult
    {
        public string RelativePath { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
    }

    public sealed class WorkspaceSearchRequest
    {
        public string ProjectPath { get; set; } = string.Empty;
        public string Query { get; set; } = string.Empty;
        public string Glob { get; set; } = "**/*";
        public int MaxResults { get; set; } = 200;
    }

    public sealed class WorkspaceSearchMatch
    {
        public string RelativePath { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public string Line { get; set; } = string.Empty;
    }

    public sealed class WorkspaceSearchResult
    {
        public List<WorkspaceSearchMatch> Matches { get; set; } = new List<WorkspaceSearchMatch>();
        public bool Truncated { get; set; }
    }

    public sealed class WorkspaceFingerprintRequest
    {
        public string ProjectPath { get; set; } = string.Empty;
        public List<string> RelativePaths { get; set; } = new List<string>();
    }

    public sealed class WorkspaceFileFingerprint
    {
        public string RelativePath { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public long Length { get; set; }
    }

    public sealed class WorkspaceFingerprintResult
    {
        public List<WorkspaceFileFingerprint> Files { get; set; } = new List<WorkspaceFileFingerprint>();
    }

    public sealed class WorkspaceChangeRequest
    {
        public string ProjectPath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string? ExpectedSha256 { get; set; }
        public bool CreateIfMissing { get; set; }
        public bool DryRun { get; set; }
    }

    public sealed class WorkspaceChangeResult
    {
        public string RelativePath { get; set; } = string.Empty;
        public string? PreviousSha256 { get; set; }
        public string NewSha256 { get; set; } = string.Empty;
        public bool Changed { get; set; }
    }
}
