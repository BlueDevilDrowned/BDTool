namespace BDTool.Contracts.Errors
{
    /// <summary>
    /// CLI 稳定退出码。值一旦发布不得重排或复用。
    /// </summary>
    public enum CliExitCode
    {
        /// <summary>成功。</summary>
        Success = 0,

        /// <summary>参数或输入格式错误。</summary>
        InvalidArgument = 2,

        /// <summary>当前 Provider 或构建不支持所需能力。</summary>
        CapabilityUnsupported = 3,

        /// <summary>项目、Editor 或运行环境未就绪。</summary>
        EnvironmentNotReady = 4,

        /// <summary>执行失败。</summary>
        ExecutionFailed = 5,

        /// <summary>执行超时。</summary>
        Timeout = 6,

        /// <summary>调用方取消。</summary>
        Cancelled = 7,

        /// <summary>结果不确定，禁止按成功处理。</summary>
        ExecutionUncertain = 8
    }
}
