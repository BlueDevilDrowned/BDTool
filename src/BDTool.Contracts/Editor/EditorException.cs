using System;
using System.Collections.Generic;

namespace BDTool.Contracts.Editor
{
    /// <summary>
    /// Editor Provider 的稳定失败载体。Provider 可以把上游进程退出码、
    /// stderr 和结构化错误转换成本类型；Operations 只需依赖 Contracts，
    /// 无需引用具体 Provider 或解析品牌私有异常。
    /// </summary>
    public sealed class EditorException : Exception
    {
        public EditorException(
            string code,
            string message,
            IReadOnlyDictionary<string, string>? details = null)
            : base(message)
        {
            Code = string.IsNullOrWhiteSpace(code) ? "EXECUTION_FAILED" : code;
            Details = details ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        /// <summary>BDTool 公共错误码。</summary>
        public string Code { get; }

        /// <summary>不包含敏感输出的结构化诊断。</summary>
        public IReadOnlyDictionary<string, string> Details { get; }
    }
}
