using BDTool.Contracts;

namespace BDTool.Presentation.States;

/// <summary>
/// 状态映射的唯一实现。页面只消费语义值，避免同一状态在不同模块使用不同颜色或文字。
/// </summary>
public static class StatusSemantics
{
    public static StatusSemantic FromOperationStatus(OperationStatus status) => status switch
    {
        OperationStatus.Completed => StatusSemantic.Success,
        OperationStatus.Running or OperationStatus.Queued => StatusSemantic.Running,
        OperationStatus.Uncertain => StatusSemantic.Uncertain,
        OperationStatus.Cancelled => StatusSemantic.Cancelled,
        _ => StatusSemantic.Failed
    };

    public static StatusSemantic FromText(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return StatusSemantic.Neutral;
        }

        return status.Trim() switch
        {
            "Ready" or "Completed" or "Passed" => StatusSemantic.Success,
            "Running" or "ManualInProgress" or "Dispatching" or "Reviewing" => StatusSemantic.Running,
            "Warning" or "NeedsRework" or "CancelRequested" or "CancelUnconfirmed" => StatusSemantic.Warning,
            "Failed" or "Blocked" => StatusSemantic.Failed,
            "Uncertain" or "DeliveryUnknown" => StatusSemantic.Uncertain,
            "Unsupported" => StatusSemantic.Unsupported,
            "Cancelled" => StatusSemantic.Cancelled,
            _ => StatusSemantic.Neutral
        };
    }

    public static string ToDisplayText(StatusSemantic semantic) => semantic switch
    {
        StatusSemantic.Success => "已完成",
        StatusSemantic.Running => "运行中",
        StatusSemantic.Warning => "需处理",
        StatusSemantic.Failed => "失败",
        StatusSemantic.Uncertain => "结果不确定",
        StatusSemantic.Unsupported => "不支持",
        StatusSemantic.Cancelled => "已取消",
        _ => "未知"
    };
}
