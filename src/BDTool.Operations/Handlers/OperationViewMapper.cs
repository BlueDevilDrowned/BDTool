using System.Collections.Generic;
using BDTool.Contracts.Operations;

namespace BDTool.Operations.Handlers;

/// <summary>把冻结的目录描述映射为公开摘要/详情，不做能力推断。</summary>
internal static class OperationViewMapper
{
    public static OperationSummary ToSummary(OperationDescriptor descriptor)
    {
        return new OperationSummary
        {
            Id = descriptor.Id,
            Description = descriptor.Description,
            Effect = descriptor.Effect,
            Environment = new List<string>(descriptor.Environment),
            RequiredCapabilities = new List<string>(descriptor.RequiredCapabilities),
            TimeoutSeconds = descriptor.TimeoutSeconds,
            Artifacts = new List<string>(descriptor.Artifacts),
            Implemented = descriptor.Implemented,
            Milestone = descriptor.Milestone,
            Availability = descriptor.Availability,
            Notes = descriptor.Notes
        };
    }

    public static OperationDetail ToDetail(OperationDescriptor descriptor)
    {
        var summary = ToSummary(descriptor);
        return new OperationDetail
        {
            Id = summary.Id,
            Description = summary.Description,
            Effect = summary.Effect,
            Environment = summary.Environment,
            RequiredCapabilities = summary.RequiredCapabilities,
            TimeoutSeconds = summary.TimeoutSeconds,
            Artifacts = summary.Artifacts,
            Implemented = summary.Implemented,
            Milestone = summary.Milestone,
            Availability = summary.Availability,
            Notes = summary.Notes,
            InputSchemaJson = descriptor.InputSchemaJson,
            ResultSchemaJson = descriptor.ResultSchemaJson
        };
    }
}
