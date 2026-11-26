using BDTool.Contracts.Operations;

namespace BDTool.Application;

/// <summary>
/// 为 Handler 创建受控基础设施上下文。M1 组合根装配真实 Workspace、Artifact 与能力检查；
/// Editor 使用访问即抛错的占位实现。返回 null 表示基础设施未装配，
/// 网关必须据此返回明确的不支持结果，不能用空上下文伪造执行成功。
/// </summary>
public interface IOperationContextFactory
{
    IOperationContext? TryCreate(OperationRequest request);
}
