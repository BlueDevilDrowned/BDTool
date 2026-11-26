using BDTool.Contracts;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Operations;

namespace BDTool.Provider.Official;

/// <summary>官方 Editor Job 的进程内生命周期与协作取消状态。</summary>
internal sealed class OfficialEditorJob : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource<EditorOperationResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _finished;
    private int _disposed;

    public OfficialEditorJob(
        string jobId,
        string operationId,
        bool hasWriteRisk)
    {
        JobId = jobId;
        OperationId = operationId;
        HasWriteRisk = hasWriteRisk;
    }

    public string JobId { get; }
    public string OperationId { get; }
    public bool HasWriteRisk { get; }
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
