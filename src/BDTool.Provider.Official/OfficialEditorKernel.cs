using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using BDTool.Contracts;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;

namespace BDTool.Provider.Official;

/// <summary>
/// Unity 官方 `com.unity.pipeline` Provider。它读取官方实例描述并通过本机 HTTP
/// 执行公开命令；业务 Handler 与 Unity CLI/Provider 私有参数保持隔离。
/// </summary>
public sealed class OfficialEditorKernel : IEditorKernel, IDisposable
{
    private const string ImplementationVersion = "0.10.0-exp.1";
    private const string StatusStage = "editor.status";
    private const string ExecuteStage = "editor.command";

    private static readonly IReadOnlyDictionary<string, OperationSupport> Operations =
        new Dictionary<string, OperationSupport>(StringComparer.Ordinal)
        {
            ["project.inspect"] = new("scene get_hierarchy", ExpectedMutating: false),
            ["project.asset-snapshot"] = new("script eval", ExpectedMutating: false),
            ["project.prefab-set-active"] = new("script eval", ExpectedMutating: true)
        };

    private readonly OfficialEditorOptions _options;
    private readonly IOfficialPipelineClient _client;
    private readonly OfficialOperationExecutor _executor;
    private readonly ConcurrentDictionary<string, OfficialEditorJob> _jobs =
        new(StringComparer.Ordinal);
    private int _disposed;

    public OfficialEditorKernel(OfficialEditorOptions options)
        : this(options, new OfficialPipelineClient())
    {
    }

    internal OfficialEditorKernel(
        OfficialEditorOptions options,
        IOfficialPipelineClient client)
    {
        ArgumentNullException.ThrowIfNull(options);
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
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _executor = new OfficialOperationExecutor(_client);
    }

    public Task<ProviderDescriptor> DescribeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProviderDescriptor
        {
            Id = "official",
            DisplayName = "Unity Pipeline Editor",
            Kind = ProviderKinds.Editor,
            ContractVersion = BDToolContract.Version,
            ImplementationVersion = ImplementationVersion,
            UnityRange = ">=6000.0",
            Capabilities = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [CapabilityIds.EditorStatus] = true,
                [CapabilityIds.EditorToolRead] = true,
                [CapabilityIds.EditorToolWrite] = true,
                [CapabilityIds.EditorJob] = true,
                [CapabilityIds.EditorCancel] = true
            },
            Limits = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["maxTimeoutMilliseconds"] =
                    _options.DefaultTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture),
                ["completedJobRetentionMilliseconds"] =
                    _options.CompletedJobRetentionMilliseconds.ToString(CultureInfo.InvariantCulture),
                ["transport"] = "http",
                ["package"] = "com.unity.pipeline@" + ImplementationVersion
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
            var status = await _client.GetStatusAsync(
                    projectPath,
                    _options.DefaultTimeoutMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);
            return MapStatus(status, projectPath);
        }
        catch (OfficialPipelineException exception) when (exception.Code == ErrorCodes.EditorNotFound)
        {
            return new EditorStatus
            {
                State = "not-found",
                ProjectPath = projectPath,
                Message = exception.Message
            };
        }
        catch (OfficialPipelineException exception) when (exception.Code == ErrorCodes.EditorDisconnected)
        {
            return new EditorStatus
            {
                State = "stopped",
                ProjectPath = projectPath,
                Message = exception.Message
            };
        }
        catch (OfficialPipelineException exception)
        {
            throw OfficialErrorMapper.ToEditorException(exception, StatusStage);
        }
    }

    public async Task<OperationHandle> SubmitAsync(
        EditorOperationRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);

        var operationId = request.OperationId ?? string.Empty;
        if (!Operations.TryGetValue(operationId, out var support))
        {
            throw new EditorException(
                ErrorCodes.CapabilityUnsupported,
                "Official Provider 不支持该 Editor 操作。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["operationId"] = operationId,
                    ["supportedOperation"] = string.Join(",", Operations.Keys)
                });
        }

        if (request.Mutating != support.ExpectedMutating
            || (support.ExpectedMutating && request.WriteScope.Count == 0))
        {
            throw new EditorException(
                ErrorCodes.InvalidArgument,
                "Editor 请求的写入意图与操作声明不一致。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["operationId"] = operationId,
                    ["mutating"] = request.Mutating ? "true" : "false"
                });
        }

        var projectPath = ResolveRequestedProjectPath(request.ProjectPath);
        var timeoutMilliseconds = ResolveTimeoutMilliseconds(request.TimeoutSeconds);
        EditorStatus status;
        try
        {
            status = await GetStatusAsync(
                    new EditorStatusRequest { ProjectPath = projectPath },
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureReadyForToolCall(status);
            _executor.ValidateInput(request);
        }
        catch (EditorException)
        {
            throw;
        }
        catch (OfficialPipelineException exception)
        {
            throw OfficialErrorMapper.ToEditorException(exception, ExecuteStage);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            throw new EditorException(
                ErrorCodes.InvalidArgument,
                "Official Provider 操作输入无效：" + exception.Message,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["operationId"] = operationId
                });
        }

        var hasWriteRisk = support.ExpectedMutating && !request.DryRun;
        var job = new OfficialEditorJob(
            "official-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            operationId,
            hasWriteRisk);
        if (!_jobs.TryAdd(job.JobId, job))
        {
            job.Dispose();
            throw new EditorException(ErrorCodes.InternalError, "无法登记新的 Official Editor Job。");
        }

        _ = RunJobAsync(job, request, status, projectPath, timeoutMilliseconds);
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
                "Official Editor Job 不存在或已完成结果保留期。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["jobId"] = handle.JobId ?? string.Empty
                });
        }

        if (!job.Completion.IsCompleted)
        {
            return new EditorOperationResult
            {
                Handle = handle,
                Status = OperationStatus.Running
            };
        }

        return await job.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(OperationHandle handle, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(handle);
        if (!_jobs.TryGetValue(handle.JobId ?? string.Empty, out var job))
        {
            throw new EditorException(ErrorCodes.NotFound, "Official Editor Job 不存在。");
        }

        if (!job.TryRequestCancellation())
        {
            return;
        }

        try
        {
            await job.Completion
                .WaitAsync(TimeSpan.FromMilliseconds(_options.CancelWaitMilliseconds), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 取消请求已送达 HTTP 客户端；服务端是否已停止由后续结果/回读判定。
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var job in _jobs.Values)
        {
            job.TryRequestCancellation();
            job.Dispose();
        }

        _jobs.Clear();
        _client.Dispose();
    }

    private async Task RunJobAsync(
        OfficialEditorJob job,
        EditorOperationRequest request,
        EditorStatus status,
        string projectPath,
        int timeoutMilliseconds)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(job.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMilliseconds));
        try
        {
            var output = await _executor.ExecuteAsync(
                    request,
                    status,
                    projectPath,
                    timeoutMilliseconds,
                    timeout.Token)
                .ConfigureAwait(false);
            job.Complete(new EditorOperationResult
            {
                Handle = job.CreateHandle(OperationStatus.Completed),
                Status = OperationStatus.Completed,
                OutputJson = output
            });
        }
        catch (OperationCanceledException)
        {
            if (job.HasWriteRisk)
            {
                job.Complete(OfficialEditorResults.Uncertain(
                    ErrorCodes.ExecutionUncertain,
                    "Official Editor 写入已提交但请求中断，无法确认资产状态。"));
            }
            else
            {
                var cancelled = job.CancellationToken.IsCancellationRequested;
                job.Complete(OfficialEditorResults.Failure(
                    cancelled ? ErrorCodes.Cancelled : ErrorCodes.Timeout,
                    cancelled ? "Official Editor 操作已取消。" : "Official Editor 操作超时。",
                    cancelled ? OperationStatus.Cancelled : OperationStatus.Failed));
            }
        }
        catch (OfficialPipelineException exception)
        {
            if (job.HasWriteRisk && !OfficialEditorResults.IsSafeWriteFailure(exception.Code))
            {
                job.Complete(OfficialEditorResults.Uncertain(
                    ErrorCodes.ExecutionUncertain,
                    "Official Editor 写入结果无法确认：" + exception.Message));
            }
            else
            {
                job.Complete(OfficialEditorResults.Failure(
                    exception.Code,
                    exception.Message,
                    OperationStatus.Failed));
            }
        }
        catch (Exception exception)
        {
            if (job.HasWriteRisk)
            {
                job.Complete(OfficialEditorResults.Uncertain(
                    ErrorCodes.ExecutionUncertain,
                    "Official Editor 写入结果无法确认：" + exception.Message));
            }
            else
            {
                job.Complete(OfficialEditorResults.Failure(
                    ErrorCodes.ExecutionFailed,
                    "Official Editor 执行失败：" + exception.Message,
                    OperationStatus.Failed));
            }
        }

        await RetainCompletedJobAsync(job).ConfigureAwait(false);
    }

    private async Task RetainCompletedJobAsync(OfficialEditorJob job)
    {
        if (_options.CompletedJobRetentionMilliseconds > 0)
        {
            await Task.Delay(_options.CompletedJobRetentionMilliseconds).ConfigureAwait(false);
        }

        _jobs.TryRemove(job.JobId, out _);
        job.Dispose();
    }

    private EditorStatus MapStatus(OfficialPipelineStatusSnapshot snapshot, string requestedProjectPath)
    {
        return new EditorStatus
        {
            State = NormalizeState(snapshot.State),
            ProjectPath = requestedProjectPath,
            UnityVersion = snapshot.UnityVersion,
            ProcessId = snapshot.ProcessId,
            Message = snapshot.Message
        };
    }

    private static string NormalizeState(string state) =>
        state.Trim().ToLowerInvariant().Replace('-', '_') switch
        {
            "ready" => "ready",
            "starting" => "starting",
            "compiling" => "compiling",
            "stopped" => "stopped",
            "safe_mode" => "safe_mode",
            _ => "unknown"
        };

    private static void EnsureReadyForToolCall(EditorStatus status)
    {
        if (string.Equals(status.State, "ready", StringComparison.Ordinal))
        {
            return;
        }

        var code = string.Equals(status.State, "not-found", StringComparison.Ordinal)
            ? ErrorCodes.EditorNotFound
            : string.Equals(status.State, "stopped", StringComparison.Ordinal)
                ? ErrorCodes.EditorDisconnected
                : ErrorCodes.EditorNotReady;
        throw new EditorException(
            code,
            string.IsNullOrWhiteSpace(status.Message)
                ? "Unity Editor 当前不可执行命令。"
                : status.Message,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["state"] = status.State,
                ["projectPath"] = status.ProjectPath
            });
    }

    private string ResolveRequestedProjectPath(string? requestedProjectPath)
    {
        var requested = string.IsNullOrWhiteSpace(requestedProjectPath)
            ? _options.ProjectPath
            : requestedProjectPath;
        var normalized = Path.GetFullPath(requested);
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(normalized),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.ProjectPath)),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new EditorException(
                ErrorCodes.ProjectNotFound,
                "Official Provider 不能跨绑定的项目根执行命令。");
        }

        return normalized;
    }

    private int ResolveTimeoutMilliseconds(int requestTimeoutSeconds)
    {
        if (requestTimeoutSeconds <= 0)
        {
            return _options.DefaultTimeoutMilliseconds;
        }

        var requested = (long)requestTimeoutSeconds * 1000L;
        return (int)Math.Clamp(
            Math.Min(requested, _options.DefaultTimeoutMilliseconds),
            1L,
            int.MaxValue);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }

    private sealed record OperationSupport(string Command, bool ExpectedMutating);
}
