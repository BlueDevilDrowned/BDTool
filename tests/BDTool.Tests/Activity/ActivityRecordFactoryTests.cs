using System;
using BDTool.Activity;
using BDTool.Contracts;
using BDTool.Contracts.Activity;
using BDTool.Contracts.Gateway;
using BDTool.Contracts.Operations;

namespace BDTool.Tests.Activity;

/// <summary>验证 Activity 输出预览与 CLI、落盘记录保持同一套 camelCase 序列化口径。</summary>
public sealed class ActivityRecordFactoryTests
{
    [Fact]
    public void Complete_OutputPreviewUsesCamelCaseAndStringEnums()
    {
        var record = ActivityRecordFactory.CreateStarted(
            new ActivityContext { ActivityId = "act-preview", TraceId = "trace-preview" },
            new OperationRequest { OperationId = "system.doctor" },
            new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero));
        var result = new OperationResult { Status = OperationStatus.Completed };

        ActivityRecordFactory.Complete(
            record,
            result,
            0,
            new DateTimeOffset(2026, 11, 26, 0, 0, 1, TimeSpan.Zero));

        Assert.Contains("\"status\":\"completed\"", record.Output.PreviewJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Status\"", record.Output.PreviewJson, StringComparison.Ordinal);
    }
}
