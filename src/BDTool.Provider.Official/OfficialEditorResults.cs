using BDTool.Contracts;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;

namespace BDTool.Provider.Official;

/// <summary>Official Provider 的稳定失败与不确定结果构造。</summary>
internal static class OfficialEditorResults
{
    public static EditorOperationResult Failure(
        string code,
        string message,
        OperationStatus status)
    {
        var result = new EditorOperationResult { Status = status };
        result.Errors.Add(new KernelError
        {
            Code = code,
            Stage = "editor.command",
            Message = message,
            Retryable = false
        });
        return result;
    }

    public static EditorOperationResult Uncertain(string code, string message)
    {
        var result = new EditorOperationResult { Status = OperationStatus.Uncertain };
        result.Errors.Add(new KernelError
        {
            Code = code,
            Stage = "editor.command",
            Message = message,
            Retryable = false
        });
        return result;
    }

    public static bool IsSafeWriteFailure(string code) =>
        code is ErrorCodes.InvalidArgument
            or ErrorCodes.NotFound
            or ErrorCodes.PreconditionFailed
            or ErrorCodes.CapabilityUnsupported;
}
