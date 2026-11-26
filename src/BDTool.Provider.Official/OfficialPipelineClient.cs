using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BDTool.Contracts.Errors;

namespace BDTool.Provider.Official;

/// <summary>
/// 读取官方 Pipeline 的本地实例描述并通过受 Bearer Token 保护的 HTTP API 执行命令。
/// 这里不使用 unity CLI，从而避免 CLI/包版本之间的参数兼容问题。
/// </summary>
internal sealed class OfficialPipelineClient : IOfficialPipelineClient
{
    private const string DescriptorRelativePath = "Library/Pipeline/.unity-pipeline-port";
    private static readonly TimeSpan MaxHeartbeatAge = TimeSpan.FromSeconds(60);
    private readonly HttpClient _httpClient;

    public OfficialPipelineClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<OfficialPipelineStatusSnapshot> GetStatusAsync(
        string projectPath,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var descriptor = await ReadDescriptorAsync(projectPath, cancellationToken).ConfigureAwait(false);
        using var request = CreateRequest(HttpMethod.Get, descriptor, "/api/status");
        using var timeout = CreateTimeout(cancellationToken, timeoutMilliseconds);
        using var response = await SendAsync(request, timeout.Token, "editor.status").ConfigureAwait(false);
        var body = await ReadJsonAsync(response, "editor.status").ConfigureAwait(false);
        var state = GetString(body, "status");
        if (string.IsNullOrWhiteSpace(state))
        {
            throw new OfficialPipelineException(
                ErrorCodes.ExecutionFailed,
                "Unity Pipeline status 响应缺少 status。");
        }

        return new OfficialPipelineStatusSnapshot
        {
            State = state,
            ProjectPath = descriptor.ProjectPath,
            UnityVersion = descriptor.UnityVersion,
            ProcessId = descriptor.Pid > 0 ? descriptor.Pid : null,
            Message = string.Equals(state, "ready", StringComparison.Ordinal)
                ? string.Empty
                : "Unity Editor 当前状态：" + state
        };
    }

    public async Task<JsonElement> ExecuteAsync(
        string projectPath,
        string command,
        IReadOnlyDictionary<string, object?> parameters,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var descriptor = await ReadDescriptorAsync(projectPath, cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(new
        {
            command,
            parameters
        });
        using var request = CreateRequest(HttpMethod.Post, descriptor, "/api/exec");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var timeout = CreateTimeout(cancellationToken, timeoutMilliseconds);
        using var response = await SendAsync(request, timeout.Token, "editor.command").ConfigureAwait(false);
        var body = await ReadJsonAsync(response, "editor.command").ConfigureAwait(false);

        if (!response.IsSuccessStatusCode
            || !(body.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True))
        {
            throw OfficialErrorMapper.FromResponse((int)response.StatusCode, body, "editor.command");
        }

        if (!body.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object)
        {
            throw new OfficialPipelineException(
                ErrorCodes.ExecutionFailed,
                "Unity Pipeline 命令响应缺少 result。");
        }

        // script eval 的 result 还包一层 EvalResponse；公开 Provider 只消费其中真正的 result。
        if (string.Equals(command, "script eval", StringComparison.Ordinal)
            && result.TryGetProperty("result", out var evaluated))
        {
            return evaluated.Clone();
        }

        return result.Clone();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task<OfficialPipelineDescriptor> ReadDescriptorAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        var normalizedProject = Path.GetFullPath(projectPath);
        var descriptorPath = Path.Combine(
            normalizedProject,
            DescriptorRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(descriptorPath))
        {
            throw new OfficialPipelineException(
                ErrorCodes.EditorNotFound,
                "未找到 Unity Pipeline 实例描述。请确认目标项目已安装 Pipeline 包且 Editor 正在运行。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectPath"] = normalizedProject
                });
        }

        OfficialPipelineDescriptor? descriptor;
        try
        {
            var json = await File.ReadAllTextAsync(descriptorPath, cancellationToken).ConfigureAwait(false);
            descriptor = JsonSerializer.Deserialize<OfficialPipelineDescriptor>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            throw new OfficialPipelineException(
                ErrorCodes.EditorDisconnected,
                "Unity Pipeline 实例描述不是合法 JSON。");
        }

        if (descriptor is null
            || descriptor.Port <= 0
            || string.IsNullOrWhiteSpace(descriptor.EvalToken))
        {
            throw new OfficialPipelineException(
                ErrorCodes.EditorDisconnected,
                "Unity Pipeline 实例描述缺少有效端口或认证信息。");
        }

        if (!string.Equals(
                Path.GetFullPath(descriptor.ProjectPath),
                normalizedProject,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new OfficialPipelineException(
                ErrorCodes.EditorNotFound,
                "Unity Pipeline 实例绑定的项目与请求项目不一致。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["requestedProjectPath"] = normalizedProject,
                    ["actualProjectPath"] = descriptor.ProjectPath
                });
        }

        if (descriptor.LastHeartbeat != default
            && DateTimeOffset.UtcNow - descriptor.LastHeartbeat > MaxHeartbeatAge)
        {
            throw new OfficialPipelineException(
                ErrorCodes.EditorDisconnected,
                "Unity Pipeline 实例心跳已过期。");
        }

        return descriptor;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        OfficialPipelineDescriptor descriptor,
        string relativePath)
    {
        var request = new HttpRequestMessage(
            method,
            new Uri("http://127.0.0.1:" + descriptor.Port + relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", descriptor.EvalToken);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        string stage)
    {
        try
        {
            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new OfficialPipelineException(
                ErrorCodes.EditorDisconnected,
                "无法连接 Unity Pipeline：" + exception.Message,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["stage"] = stage });
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(
        HttpResponseMessage response,
        string stage)
    {
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        JsonElement element;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            element = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new OfficialPipelineException(
                ErrorCodes.ExecutionFailed,
                "Unity Pipeline 返回了非 JSON 响应。",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["stage"] = stage });
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw OfficialErrorMapper.FromResponse((int)response.StatusCode, element, stage);
        }

        return element;
    }

    private static CancellationTokenSource CreateTimeout(
        CancellationToken cancellationToken,
        int timeoutMilliseconds)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Clamp(timeoutMilliseconds, 1, int.MaxValue));
        return timeout;
    }

    private static string? GetString(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
