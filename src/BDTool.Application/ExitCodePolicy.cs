using BDTool.Contracts;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;

namespace BDTool.Application;

/// <summary>
/// 把操作结果映射为稳定 CLI 退出码。映射规则集中在一处，
/// 防止 CLI、脚本与测试对同一状态给出不同退出码。
/// </summary>
public static class ExitCodePolicy
{
    /// <summary>根据结果状态与首个错误码计算退出码。</summary>
    public static CliExitCode Map(OperationResult? result)
    {
        if (result is null)
        {
            return CliExitCode.ExecutionFailed;
        }

        switch (result.Status)
        {
            case OperationStatus.Completed:
                return CliExitCode.Success;
            case OperationStatus.Unsupported:
                return CliExitCode.CapabilityUnsupported;
            case OperationStatus.Cancelled:
                return CliExitCode.Cancelled;
            case OperationStatus.Uncertain:
                return CliExitCode.ExecutionUncertain;
            case OperationStatus.Failed:
                return MapFailed(result);
            default:
                // Queued/Running/Partial 不应作为最终结果返回；按失败处理，避免被误判为成功。
                return CliExitCode.ExecutionFailed;
        }
    }

    private static CliExitCode MapFailed(OperationResult result)
    {
        var code = result.Errors.Count > 0 ? result.Errors[0].Code : string.Empty;
        switch (code)
        {
            case ErrorCodes.InvalidArgument:
            case ErrorCodes.NotFound:
                return CliExitCode.InvalidArgument;
            case ErrorCodes.CapabilityUnsupported:
                return CliExitCode.CapabilityUnsupported;
            case ErrorCodes.ProjectNotFound:
                return CliExitCode.EnvironmentNotReady;
            case ErrorCodes.EnvironmentUnsupported:
            case ErrorCodes.ProviderNotFound:
            case ErrorCodes.ProviderVersionMismatch:
            case ErrorCodes.UnityVersionUnsupported:
            case ErrorCodes.EditorNotFound:
            case ErrorCodes.EditorNotReady:
            case ErrorCodes.EditorDisconnected:
                return CliExitCode.EnvironmentNotReady;
            case ErrorCodes.PreconditionFailed:
            case ErrorCodes.ExecutionFailed:
                return CliExitCode.ExecutionFailed;
            case ErrorCodes.Timeout:
                return CliExitCode.Timeout;
            case ErrorCodes.Cancelled:
                return CliExitCode.Cancelled;
            case ErrorCodes.ExecutionUncertain:
                return CliExitCode.ExecutionUncertain;
            default:
                return CliExitCode.ExecutionFailed;
        }
    }
}
