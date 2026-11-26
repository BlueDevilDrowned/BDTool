using System.Text.Json;

namespace BDTool.Provider.Official;

/// <summary>官方 Pipeline 的窄端口，便于对 Provider 做确定性契约测试。</summary>
internal interface IOfficialPipelineClient : IDisposable
{
    Task<OfficialPipelineStatusSnapshot> GetStatusAsync(
        string projectPath,
        int timeoutMilliseconds,
        CancellationToken cancellationToken);

    Task<JsonElement> ExecuteAsync(
        string projectPath,
        string command,
        IReadOnlyDictionary<string, object?> parameters,
        int timeoutMilliseconds,
        CancellationToken cancellationToken);
}
