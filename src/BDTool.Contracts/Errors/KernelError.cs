using System.Collections.Generic;

namespace BDTool.Contracts.Errors
{
    /// <summary>
    /// 机器可读错误。公共字段固定为 code/message/retryable/details。
    /// Details 的值在写入前统一转为字符串，避免把任意对象泄漏到协议边界。
    /// </summary>
    public sealed class KernelError
    {
        /// <summary>稳定错误码，取值见 <see cref="ErrorCodes"/>。</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>面向调用方的简短说明，不包含堆栈。</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>发生错误的阶段，例如 input、workspace、editor、activity。</summary>
        public string Stage { get; set; } = string.Empty;

        /// <summary>相同输入重试是否可能成功；写操作不得据此自动重试。</summary>
        public bool Retryable { get; set; }

        /// <summary>结构化诊断字段，值必须已脱敏并规范化为字符串。</summary>
        public Dictionary<string, string> Details { get; set; } = new Dictionary<string, string>();

        /// <summary>建议的下一步动作；没有修复动作时为 null。</summary>
        public NextAction? Next { get; set; }
    }

    /// <summary>
    /// 面向调用方的下一步动作。Command 是完整命令示例，不包含自动执行语义。
    /// </summary>
    public sealed class NextAction
    {
        public string Command { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// 非致命警告。警告不改变操作成功或失败状态，但必须随结果返回。
    /// </summary>
    public sealed class KernelWarning
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public Dictionary<string, string> Details { get; set; } = new Dictionary<string, string>();
    }
}
