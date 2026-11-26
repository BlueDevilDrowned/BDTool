namespace BDTool.Contracts
{
    /// <summary>
    /// 一次操作或 Job 的统一状态。
    /// 适配器负责把上游状态映射到这里，业务层不得依赖 Provider 私有状态值。
    /// </summary>
    public enum OperationStatus
    {
        /// <summary>已接收但尚未开始执行。</summary>
        Queued = 0,

        /// <summary>正在执行。</summary>
        Running = 1,

        /// <summary>全部目标已完成并有结果证据。</summary>
        Completed = 2,

        /// <summary>部分目标完成，剩余目标失败或未执行。</summary>
        Partial = 3,

        /// <summary>执行失败；错误必须位于错误集合中。</summary>
        Failed = 4,

        /// <summary>调用方取消；取消不等于回滚。</summary>
        Cancelled = 5,

        /// <summary>当前 Provider 或构建不具备所需能力。</summary>
        Unsupported = 6,

        /// <summary>执行结果无法确认，禁止当作成功或自动重试写操作。</summary>
        Uncertain = 7
    }
}
