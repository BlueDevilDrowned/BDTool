using System.Collections.Generic;

namespace BDTool.Contracts.Operations
{
    /// <summary>
    /// 公共操作请求。InputJson 是操作输入的唯一传输形式；
    /// Handler 必须把它解析为私有 typed 输入，不得把原始 JSON 当成业务结果。
    /// </summary>
    public sealed class OperationRequest
    {
        public string OperationId { get; set; } = string.Empty;
        public string ProjectPath { get; set; } = string.Empty;
        public string InputJson { get; set; } = "{}";
        public bool DryRun { get; set; }
        public int TimeoutSeconds { get; set; } = 60;

        /// <summary>允许写入的项目相对路径范围；空表示只读或由操作定义拒绝写入。</summary>
        public List<string> WriteScope { get; set; } = new List<string>();

        /// <summary>写入前期望的文件或资源指纹。</summary>
        public string? ExpectedFingerprint { get; set; }
    }
}
