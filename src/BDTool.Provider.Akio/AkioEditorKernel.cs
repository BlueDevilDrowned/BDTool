using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using BDTool.Contracts;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;

namespace BDTool.Provider.Akio;

/// <summary>
/// Akio Editor Provider 的进程边界实现。只解析 unity-cli 的 stdout JSON 协议，
/// 状态与错误均转换为 Contracts 中的稳定词表；业务层不接触上游原始输出。
/// </summary>
public sealed class AkioEditorKernel : IEditorKernel, IDisposable
{
    private const string StatusStage = "editor.status";
    private const string ToolSchemaStage = "editor.tool.schema";
    private const string ToolCallStage = "editor.tool.call";

    /// <summary>当前只开放经过只读 schema 校验的 inspect/probe 操作。</summary>
    private static readonly IReadOnlyDictionary<string, string> OperationTools =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["project.inspect"] = "get_hierarchy",
            ["project.probe"] = "bdtool_read_only_probe"
        };

    private readonly AkioEditorOptions _options;
    private readonly IUnityCliProcessRunner _runner;
    private readonly ConcurrentDictionary<string, EditorJob> _jobs =
        new(StringComparer.Ordinal);
    private int _disposed;

    public AkioEditorKernel(
        AkioEditorOptions options,
        IUnityCliProcessRunner? processRunner = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.UnityCliPath))
        {
            throw new ArgumentException("UnityCliPath 不能为空。", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ProjectPath))
        {
            throw new ArgumentException("ProjectPath 不能为空。", nameof(options));
        }

        if (options.DefaultTimeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "DefaultTimeoutMilliseconds 必须大于 0。");
        }

        if (options.CompletedJobRetentionMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "CompletedJobRetentionMilliseconds 不能为负数。");
        }

        if (options.CancelWaitMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "CancelWaitMilliseconds 必须大于 0。");
        }

        _options = options;
        _runner = processRunner ?? new UnityCliProcessRunner();
    }

    public Task<ProviderDescriptor> DescribeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProviderDescriptor
        {
            Id = "akio",
            DisplayName = "Akio Unity Editor",
            Kind = ProviderKinds.Editor,
            ContractVersion = BDToolContract.Version,
            ImplementationVersion = "0.2.0",
            Capabilities = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [CapabilityIds.EditorStatus] = true,
                [CapabilityIds.EditorToolRead] = true,
                [CapabilityIds.EditorJob] = true,
                [CapabilityIds.EditorCancel] = true
            },
            Limits = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["maxTimeoutMilliseconds"] =
                    _options.DefaultTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
                ["completedJobRetentionMilliseconds"] =
                    _options.CompletedJobRetentionMilliseconds.ToString(CultureInfo.InvariantCulture)
            }
        });
    }

    public async Task<EditorStatus> GetStatusAsync(
        EditorStatusRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var projectPath = ResolveRequestedProjectPath(request?.ProjectPath);
        try
        {
            return await QueryStatusAsync(
                    projectPath,
                    _options.DefaultTimeoutMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EditorException exception) when (exception.Code == ErrorCodes.EditorNotFound)
        {
            // unity-cli 用 EDITOR_NOT_FOUND 表示“项目没有可连接的 Editor”，
            // 这是状态查询的正常答案，不是 Provider 自身故障。
            return new EditorStatus
            {
                State = "not-found",
                ProjectPath = projectPath,
                Message = exception.Message
            };
        }
        catch (Exception exception)
        {
            throw MapException(exception, StatusStage, cancellationToken, string.Empty);
        }
    }

    public async Task<OperationHandle> SubmitAsync(
        EditorOperationRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);

        if (!OperationTools.TryGetValue(request.OperationId ?? string.Empty, out var toolName))
        {
            throw new EditorException(
                ErrorCodes.CapabilityUnsupported,
                "Akio Provider 暂不支持该 Editor 操作。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["operationId"] = request.OperationId ?? string.Empty,
                    ["supportedOperation"] = string.Join(",", OperationTools.Keys)
                });
        }

        var projectPath = ResolveRequestedProjectPath(request.ProjectPath);
        var inputJson = ValidateInputJson(request.InputJson);
        var timeoutMilliseconds = ResolveTimeoutMilliseconds(request.TimeoutSeconds);

        // 进程启动前完成项目身份、Editor 就绪状态与工具只读性检查。
        // 任何一项失败都不创建 Job，也不执行 tool call。
        try
        {
            var status = await QueryStatusAsync(
                    projectPath,
                    timeoutMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureReadyForToolCall(status);

            await EnsureToolIsReadOnlyAsync(
                    toolName,
                    projectPath,
                    timeoutMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw MapException(exception, ToolSchemaStage, cancellationToken, toolName);
        }

        var job = new EditorJob(
            "akio-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            request.OperationId,
            toolName);

        // Job 的 Completion 在构造时固定，并先发布到字典再启动 worker，
        // 避免调用方查到 Job 但 Completion 尚未赋值。
        if (!_jobs.TryAdd(job.JobId, job))
        {
            job.Dispose();
            throw new EditorException(
                ErrorCodes.InternalError,
                "无法登记新的 Editor Job。");
        }

        _ = RunJobAsync(job, projectPath, inputJson, timeoutMilliseconds);
        return job.CreateHandle(OperationStatus.Running);
    }

    public async Task<EditorOperationResult> GetResultAsync(
        OperationHandle handle,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(handle);

        if (!_jobs.TryGetValue(handle.JobId ?? string.Empty, out var job))
        {
            throw new EditorException(
                ErrorCodes.NotFound,
                "Editor Job 不存在或已完成结果保留期。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["jobId"] = handle.JobId ?? string.Empty
                });
        }

        if (!job.Completion.IsCompleted)
        {
            return new EditorOperationResult
            {
                Handle = job.CreateHandle(OperationStatus.Running),
                Status = OperationStatus.Running,
                OutputJson = "{}"
            };
        }

        try
        {
            var result = await job.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            return CloneResult(result);
        }
        catch (OperationCanceledException)
        {
            throw MapException(
                new OperationCanceledException(cancellationToken),
                "editor.job.result",
                cancellationToken,
                string.Empty);
        }
    }

    public async Task CancelAsync(
        OperationHandle handle,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(handle);

        if (!_jobs.TryGetValue(handle.JobId ?? string.Empty, out var job))
        {
            throw new EditorException(
                ErrorCodes.NotFound,
                "Editor Job 不存在或已完成结果保留期。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["jobId"] = handle.JobId ?? string.Empty
                });
        }

        if (!job.TryRequestCancellation())
        {
            return;
        }

        try
        {
            await job.Completion.WaitAsync(
                    TimeSpan.FromMilliseconds(_options.CancelWaitMilliseconds),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new EditorException(
                ErrorCodes.Timeout,
                "取消请求已发出，但 Editor Job 未在等待上限内结束。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stage"] = "editor.job.cancel",
                    ["reason"] = "cancel_wait_timeout",
                    ["jobId"] = job.JobId,
                    ["cancelWaitMilliseconds"] =
                        _options.CancelWaitMilliseconds.ToString(CultureInfo.InvariantCulture)
                });
        }
        catch (OperationCanceledException)
        {
            throw MapException(
                new OperationCanceledException(cancellationToken),
                "editor.job.cancel",
                cancellationToken,
                string.Empty);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 不在这里释放单个 Job 的 CTS：worker 或等待方可能仍持有 Token。
        // 每个 Job 在完成结果保留期结束后自行释放。
        foreach (var job in _jobs.Values)
        {
            job.TryRequestCancellation();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AkioEditorKernel));
        }
    }

    /// <summary>
    /// 执行已经通过只读预检的 tool call。结果先写入稳定的 Completion，
    /// 再等待保留期并从 Job 表移除；失败只保留稳定错误和脱敏诊断。
    /// </summary>
    private async Task RunJobAsync(
        EditorJob job,
        string projectPath,
        string inputJson,
        int timeoutMilliseconds)
    {
        EditorOperationResult result;
        try
        {
            var invocation = CreateInvocation(
                projectPath,
                timeoutMilliseconds,
                "tool",
                "call",
                job.ToolName,
                "--json",
                inputJson);
            var processResult = await _runner.RunAsync(
                    invocation,
                    job.CancellationToken)
                .ConfigureAwait(false);
            using var document = AkioCliProtocol.ParseSuccess(processResult, ToolCallStage);
            result = new EditorOperationResult
            {
                Handle = job.CreateHandle(OperationStatus.Completed),
                Status = OperationStatus.Completed,
                OutputJson = AkioCliProtocol.GetDataJson(document.RootElement, ToolCallStage)
            };
        }
        catch (OperationCanceledException exception)
        {
            result = CreateCancelledResult(
                job,
                MapException(exception, ToolCallStage, job.CancellationToken, job.ToolName));
        }
        catch (Exception exception)
        {
            result = CreateFailedResult(
                job,
                MapException(exception, ToolCallStage, job.CancellationToken, job.ToolName));
        }

        job.Complete(result);
        await RetainCompletedJobAsync(job).ConfigureAwait(false);
    }

    private async Task RetainCompletedJobAsync(EditorJob job)
    {
        if (_options.CompletedJobRetentionMilliseconds > 0)
        {
            await Task.Delay(_options.CompletedJobRetentionMilliseconds)
                .ConfigureAwait(false);
        }

        _jobs.TryRemove(job.JobId, out _);
        job.Dispose();
    }

    private async Task<EditorStatus> QueryStatusAsync(
        string projectPath,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var invocation = CreateInvocation(
            projectPath,
            timeoutMilliseconds,
            "editor",
            "status");
        var processResult = await _runner.RunAsync(invocation, cancellationToken)
            .ConfigureAwait(false);
        using var document = AkioCliProtocol.ParseSuccess(processResult, StatusStage);
        return ParseStatus(document.RootElement, projectPath);
    }

    /// <summary>
    /// 解析 unity-cli `editor status` 的 `data`。ready 使用真实 `pid`/`editor`，
    /// stopped/safe_mode 才读取 `diagnostic`；状态词保持上游原始词表。
    /// </summary>
    private EditorStatus ParseStatus(JsonElement root, string requestedProjectPath)
    {
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            throw AkioCliProtocol.CreateProtocolFailure(
                StatusStage,
                new UnityCliProcessResult { Stdout = root.GetRawText() },
                "missing_status_data");
        }

        var actualProjectPath = GetString(data, "projectPath");
        EnsureProjectIdentity(requestedProjectPath, actualProjectPath, StatusStage);

        var rawState = GetString(data, "state") ?? "unknown";
        var state = NormalizeState(rawState);
        var unityVersion = string.Empty;
        var message = string.Empty;
        int? processId = null;

        if (data.TryGetProperty("diagnostic", out var diagnostic)
            && diagnostic.ValueKind == JsonValueKind.Object)
        {
            if (diagnostic.TryGetProperty("project", out var project)
                && project.ValueKind == JsonValueKind.Object)
            {
                unityVersion = GetString(project, "unityVersion") ?? string.Empty;
            }

            message = GetString(diagnostic, "recovery")
                ?? GetString(diagnostic, "message")
                ?? string.Empty;

            if (state == "ready"
                && diagnostic.TryGetProperty("editor", out var diagnosticEditor)
                && diagnosticEditor.ValueKind == JsonValueKind.Object)
            {
                processId = GetInt32(diagnosticEditor, "pid");
            }
        }

        if (state == "ready")
        {
            processId = GetInt32(data, "pid") ?? processId;
            if (data.TryGetProperty("editor", out var editor)
                && editor.ValueKind == JsonValueKind.Object)
            {
                if (editor.TryGetProperty("playability", out var playability)
                    && playability.ValueKind == JsonValueKind.Object)
                {
                    message = GetString(playability, "reason") ?? message;
                }
            }
        }

        return new EditorStatus
        {
            State = state,
            ProjectPath = requestedProjectPath,
            UnityVersion = unityVersion,
            ProcessId = processId,
            Message = message
        };
    }

    private static string NormalizeState(string state)
    {
        var normalized = state.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            "ready" => "ready",
            "starting" => "starting",
            "compiling" => "compiling",
            "safe_mode" => "safe_mode",
            "stopped" => "stopped",
            _ => "unknown"
        };
    }

    private static void EnsureReadyForToolCall(EditorStatus status)
    {
        switch (status.State)
        {
            case "ready":
                return;
            case "stopped":
                throw new EditorException(
                    ErrorCodes.EditorDisconnected,
                    "项目绑定的 Unity Editor 当前未运行。",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["stage"] = StatusStage,
                        ["reason"] = "editor_stopped",
                        ["projectPath"] = status.ProjectPath
                    });
            case "not-found":
                throw new EditorException(
                    ErrorCodes.EditorNotFound,
                    "未找到项目绑定的 Unity Editor。",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["stage"] = StatusStage,
                        ["reason"] = "editor_not_found",
                        ["projectPath"] = status.ProjectPath
                    });
            default:
                throw new EditorException(
                    ErrorCodes.EditorNotReady,
                    "Unity Editor 尚未进入可执行只读工具的状态。",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["stage"] = StatusStage,
                        ["reason"] = "editor_not_ready",
                        ["state"] = status.State
                    });
        }
    }

    /// <summary>
    /// 在调用工具前读取并验证 unity-cli schema。只有名称匹配且
    /// `mutating` 明确为 false 的内置工具才允许执行。
    /// </summary>
    private async Task EnsureToolIsReadOnlyAsync(
        string toolName,
        string projectPath,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var invocation = CreateInvocation(
            projectPath,
            timeoutMilliseconds,
            "tool",
            "schema",
            toolName);
        var processResult = await _runner.RunAsync(invocation, cancellationToken)
            .ConfigureAwait(false);
        using var document = AkioCliProtocol.ParseSuccess(processResult, ToolSchemaStage);

        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            throw CreateSchemaRejection(
                toolName,
                "missing_tool_schema",
                "unity-cli 未返回工具 schema。");
        }

        var returnedName = GetString(data, "name");
        if (!string.Equals(returnedName, toolName, StringComparison.Ordinal))
        {
            throw CreateSchemaRejection(
                toolName,
                "tool_schema_mismatch",
                "unity-cli 返回的工具 schema 与请求工具不一致。",
                returnedName);
        }

        if (!data.TryGetProperty("mutating", out var mutating)
            || mutating.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw CreateSchemaRejection(
                toolName,
                "mutating_flag_missing",
                "工具 schema 缺少明确的只读标记。");
        }

        if (mutating.ValueKind == JsonValueKind.True)
        {
            throw CreateSchemaRejection(
                toolName,
                "mutating_tool",
                "拒绝执行标记为 mutating 的工具。");
        }
    }

    private static EditorException CreateSchemaRejection(
        string toolName,
        string reason,
        string message,
        string? returnedToolName = null)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stage"] = ToolSchemaStage,
            ["reason"] = reason,
            ["toolName"] = toolName
        };
        if (!string.IsNullOrWhiteSpace(returnedToolName))
        {
            details["returnedToolName"] = returnedToolName;
        }

        return new EditorException(ErrorCodes.PreconditionFailed, message, details);
    }

    private UnityCliInvocation CreateInvocation(
        string projectPath,
        int timeoutMilliseconds,
        params string[] command)
    {
        var invocation = new UnityCliInvocation
        {
            ExecutablePath = _options.UnityCliPath,
            WorkingDirectory = _options.ProjectPath,
            TimeoutMilliseconds = timeoutMilliseconds,
            UnityEditorPath = _options.UnityEditorPath
        };
        invocation.Arguments.Add("--output");
        invocation.Arguments.Add("json");
        invocation.Arguments.Add("--project-path");
        invocation.Arguments.Add(projectPath);
        invocation.Arguments.Add("--timeout-ms");
        invocation.Arguments.Add(timeoutMilliseconds.ToString(CultureInfo.InvariantCulture));
        invocation.Arguments.AddRange(command);
        return invocation;
    }

    private string ResolveRequestedProjectPath(string? requestedProjectPath)
    {
        var boundProjectPath = NormalizeProjectPath(_options.ProjectPath);
        if (string.IsNullOrWhiteSpace(requestedProjectPath))
        {
            return boundProjectPath;
        }

        var normalizedRequest = NormalizeProjectPath(requestedProjectPath);
        if (!AreSameProject(normalizedRequest, boundProjectPath))
        {
            throw new EditorException(
                ErrorCodes.InvalidArgument,
                "请求项目与 Akio Provider 绑定的项目不一致。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stage"] = "input",
                    ["reason"] = "request_project_mismatch",
                    ["requestedProjectPath"] = normalizedRequest,
                    ["boundProjectPath"] = boundProjectPath
                });
        }

        return boundProjectPath;
    }

    private int ResolveTimeoutMilliseconds(int timeoutSeconds)
    {
        if (timeoutSeconds <= 0)
        {
            return _options.DefaultTimeoutMilliseconds;
        }

        var milliseconds = (long)timeoutSeconds * 1000L;
        return (int)Math.Clamp(milliseconds, 1L, int.MaxValue);
    }

    private static string ValidateInputJson(string? inputJson)
    {
        var value = string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new EditorException(
                    ErrorCodes.InvalidArgument,
                    "Editor 工具输入必须是 JSON 对象。",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["stage"] = "input",
                        ["reason"] = "input_not_object"
                    });
            }

            return value;
        }
        catch (JsonException)
        {
            throw new EditorException(
                ErrorCodes.InvalidArgument,
                "Editor 工具输入不是合法 JSON。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stage"] = "input",
                    ["reason"] = "invalid_json"
                });
        }
    }

    private static void EnsureProjectIdentity(
        string requestedProjectPath,
        string? actualProjectPath,
        string stage)
    {
        if (string.IsNullOrWhiteSpace(actualProjectPath))
        {
            throw new EditorException(
                ErrorCodes.PreconditionFailed,
                "Editor 响应缺少项目身份，拒绝接受该状态。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stage"] = stage,
                    ["reason"] = "project_identity_missing",
                    ["expectedProjectPath"] = requestedProjectPath
                });
        }

        var normalizedActual = NormalizeProjectPath(actualProjectPath);
        if (!AreSameProject(requestedProjectPath, normalizedActual))
        {
            throw new EditorException(
                ErrorCodes.PreconditionFailed,
                "Editor 响应的项目与 Provider 绑定项目不一致。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stage"] = stage,
                    ["reason"] = "project_mismatch",
                    ["expectedProjectPath"] = requestedProjectPath,
                    ["actualProjectPath"] = normalizedActual
                });
        }
    }

    private static string NormalizeProjectPath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        if (OperatingSystem.IsWindows())
        {
            trimmed = RemoveWindowsExtendedLengthPrefix(trimmed);
        }

        try
        {
            trimmed = Path.GetFullPath(trimmed);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or NotSupportedException
                                          or PathTooLongException)
        {
            // 非法路径保留原值，让身份比较或调用方参数校验给出稳定失败。
        }

        return trimmed.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// unity-cli 在部分 Windows 项目上会返回 extended-length 形式。
    /// 这里只做路径语法转换，不访问文件系统，也不解析符号链接真实目标。
    /// </summary>
    private static string RemoveWindowsExtendedLengthPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        const string extendedPrefix = @"\\?\";
        return path.StartsWith(extendedPrefix, StringComparison.Ordinal)
            ? path[extendedPrefix.Length..]
            : path;
    }

    private static bool AreSameProject(string left, string right) =>
        string.Equals(
            NormalizeProjectPath(left),
            NormalizeProjectPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    /// <summary>
    /// 把运行器异常收敛为稳定公共错误码。调用方取消保持 CANCELLED，
    /// 内部等待超时使用 TIMEOUT，进程无法启动使用 EDITOR_NOT_FOUND。
    /// </summary>
    private static EditorException MapException(
        Exception exception,
        string stage,
        CancellationToken cancellationToken,
        string toolName)
    {
        if (exception is EditorException editorException)
        {
            return editorException;
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["stage"] = stage,
            ["exceptionType"] = exception.GetType().Name
        };
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            details["toolName"] = toolName;
        }

        if (exception is OperationCanceledException)
        {
            details["reason"] = cancellationToken.IsCancellationRequested
                ? "caller_cancelled"
                : "operation_cancelled";
            return new EditorException(
                ErrorCodes.Cancelled,
                "Editor 操作已取消。",
                details);
        }

        if (exception is TimeoutException)
        {
            details["reason"] = "timeout";
            return new EditorException(
                ErrorCodes.Timeout,
                "Editor 操作在超时上限内未完成。",
                details);
        }

        if (exception is EditorStartException)
        {
            details["reason"] = "process_start_failed";
            return new EditorException(
                ErrorCodes.EditorNotFound,
                "无法启动 unity-cli 进程。",
                details);
        }

        details["reason"] = "unexpected_exception";
        return new EditorException(
            ErrorCodes.ExecutionFailed,
            "Editor Provider 执行失败。",
            details);
    }

    private static EditorOperationResult CreateFailedResult(
        EditorJob job,
        EditorException exception) =>
        CreateErrorResult(job, OperationStatus.Failed, exception);

    private static EditorOperationResult CreateCancelledResult(
        EditorJob job,
        EditorException exception) =>
        CreateErrorResult(job, OperationStatus.Cancelled, exception);

    private static EditorOperationResult CreateErrorResult(
        EditorJob job,
        OperationStatus status,
        EditorException exception)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in exception.Details)
        {
            details[pair.Key] = pair.Value;
        }

        var stage = details.TryGetValue("stage", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : "editor";
        var result = new EditorOperationResult
        {
            Handle = job.CreateHandle(status),
            Status = status,
            OutputJson = "{}"
        };
        result.Errors.Add(new KernelError
        {
            Code = exception.Code,
            Message = exception.Message,
            Stage = stage,
            Retryable = exception.Code is ErrorCodes.Timeout
                or ErrorCodes.EditorDisconnected
                or ErrorCodes.EditorNotReady,
            Details = details
        });
        return result;
    }

    private static EditorOperationResult CloneResult(EditorOperationResult source)
    {
        var clone = new EditorOperationResult
        {
            Handle = new OperationHandle
            {
                JobId = source.Handle.JobId,
                OperationId = source.Handle.OperationId,
                Status = source.Handle.Status
            },
            Status = source.Status,
            OutputJson = source.OutputJson
        };
        foreach (var error in source.Errors)
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in error.Details)
            {
                details[pair.Key] = pair.Value;
            }

            clone.Errors.Add(new KernelError
            {
                Code = error.Code,
                Message = error.Message,
                Stage = error.Stage,
                Retryable = error.Retryable,
                Details = details
            });
        }

        return clone;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt32(out var value)
            ? value
            : null;

    /// <summary>
    /// 单个 Editor Job 的生命周期状态。Completion 在构造时创建且不可替换；
    /// 取消请求与 CTS 释放由锁串行化，完成结果一旦发布即可被并发查询。
    /// </summary>
    private sealed class EditorJob : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<EditorOperationResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _finished;
        private int _disposed;

        public EditorJob(string jobId, string operationId, string toolName)
        {
            JobId = jobId;
            OperationId = operationId;
            ToolName = toolName;
        }

        public string JobId { get; }
        public string OperationId { get; }
        public string ToolName { get; }
        public CancellationToken CancellationToken => _cancellation.Token;
        public Task<EditorOperationResult> Completion => _completion.Task;

        public OperationHandle CreateHandle(OperationStatus status) => new()
        {
            JobId = JobId,
            OperationId = OperationId,
            Status = status
        };

        public bool TryRequestCancellation()
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _finished) != 0
                    || Volatile.Read(ref _disposed) != 0)
                {
                    return false;
                }

                _cancellation.Cancel();
                return true;
            }
        }

        public void Complete(EditorOperationResult result)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0)
            {
                return;
            }

            _completion.TrySetResult(result);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }
            }

            _cancellation.Dispose();
        }
    }
}
