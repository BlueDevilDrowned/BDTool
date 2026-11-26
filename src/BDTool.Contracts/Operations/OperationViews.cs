using System.Collections.Generic;

namespace BDTool.Contracts.Operations
{
    /// <summary>
    /// operations.list 的数据载荷。使用固定 DTO，避免业务结果退化为通用对象字典。
    /// </summary>
    public sealed class OperationsListData
    {
        public List<OperationSummary> Operations { get; set; } = new List<OperationSummary>();
    }

    /// <summary>
    /// 目录条目的公开摘要。字段名称与 <see cref="OperationDescriptor"/> 保持一致，
    /// 便于调用方在不读取原始目录文件的情况下做稳定展示。
    /// </summary>
    public class OperationSummary
    {
        public string Id { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Effect { get; set; } = OperationEffects.Read;
        public List<string> Environment { get; set; } = new List<string>();
        public List<string> RequiredCapabilities { get; set; } = new List<string>();
        public int TimeoutSeconds { get; set; } = 60;
        public List<string> Artifacts { get; set; } = new List<string>();
        public bool Implemented { get; set; }
        public string Milestone { get; set; } = string.Empty;
        public string Availability { get; set; } = OperationAvailability.Planned;
        public string Notes { get; set; } = string.Empty;
    }

    /// <summary>operations.describe 的数据载荷。</summary>
    public sealed class OperationsDescribeData
    {
        public OperationDetail Operation { get; set; } = new OperationDetail();
    }

    /// <summary>
    /// 目录条目详情。Schema 仍是 JSON 文本，Contracts 不引入 JSON 序列化依赖。
    /// </summary>
    public sealed class OperationDetail : OperationSummary
    {
        public string InputSchemaJson { get; set; } = "{}";
        public string ResultSchemaJson { get; set; } = "{}";
    }
}
