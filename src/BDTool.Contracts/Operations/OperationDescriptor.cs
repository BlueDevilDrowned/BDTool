using System.Collections.Generic;

namespace BDTool.Contracts.Operations
{
    /// <summary>
    /// 操作允许的效果。必须是完整集合中的一个稳定字符串。
    /// </summary>
    public static class OperationEffects
    {
        public const string Read = "read";
        public const string WorkspaceWrite = "workspace-write";
        public const string EditorWrite = "editor-write";
        public const string Runtime = "runtime";
        public const string Build = "build";
        public const string Destructive = "destructive";

        public static readonly IReadOnlyList<string> All = new[]
        {
            Read, WorkspaceWrite, EditorWrite, Runtime, Build, Destructive
        };
    }

    /// <summary>操作执行环境常量。</summary>
    public static class OperationEnvironments
    {
        public const string Workspace = "workspace";
        public const string Editor = "editor";

        public static readonly IReadOnlyList<string> All = new[] { Workspace, Editor };
    }

    /// <summary>
    /// 操作目录条目的可用性。contract-only 表示契约已冻结但没有 Handler；
    /// planned 表示后续里程碑；available 只能由已注册并验证的构建使用。
    /// </summary>
    public static class OperationAvailability
    {
        public const string ContractOnly = "contract-only";
        public const string Planned = "planned";
        public const string Available = "available";

        public static readonly IReadOnlyList<string> All = new[] { ContractOnly, Planned, Available };
    }

    /// <summary>
    /// 操作目录中的不可变描述（通过属性赋值构建）。
    /// Schema 在契约层保存为 JSON 文本，避免 Contracts 依赖任何 JSON 库。
    /// </summary>
    public sealed class OperationDescriptor
    {
        public string Id { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>效果值，取值见 <see cref="OperationEffects"/>。</summary>
        public string Effect { get; set; } = OperationEffects.Read;

        /// <summary>执行环境，取值见 <see cref="OperationEnvironments"/>。</summary>
        public List<string> Environment { get; set; } = new List<string>();

        public List<string> RequiredCapabilities { get; set; } = new List<string>();

        /// <summary>输入 JSON Schema 的原文。</summary>
        public string InputSchemaJson { get; set; } = "{}";

        /// <summary>结果 JSON Schema 的原文。</summary>
        public string ResultSchemaJson { get; set; } = "{}";

        public int TimeoutSeconds { get; set; } = 60;
        public List<string> Artifacts { get; set; } = new List<string>();

        /// <summary>当前构建是否注册了可执行 Handler。</summary>
        public bool Implemented { get; set; }

        /// <summary>计划里程碑，例如 M0、M1。</summary>
        public string Milestone { get; set; } = string.Empty;

        /// <summary>可用性，取值见 <see cref="OperationAvailability"/>。</summary>
        public string Availability { get; set; } = OperationAvailability.Planned;

        public string Notes { get; set; } = string.Empty;
    }

    /// <summary>操作目录的只读查询端口。</summary>
    public interface IOperationCatalog
    {
        IReadOnlyList<OperationDescriptor> GetAll();
        OperationDescriptor? Find(string operationId);
    }
}
