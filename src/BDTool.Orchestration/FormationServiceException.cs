namespace BDTool.Orchestration;

/// <summary>
/// Formation 领域失败的稳定异常类型。服务边界统一把它映射为公共错误码，
/// 避免仓储、状态机和路径校验各自定义不兼容的失败协议。
/// </summary>
internal sealed class FormationServiceException : Exception
{
    public FormationServiceException(
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details = null,
        bool retryable = false)
        : base(message)
    {
        Code = code;
        Details = details ?? new Dictionary<string, string>(StringComparer.Ordinal);
        Retryable = retryable;
    }

    public string Code { get; }

    public IReadOnlyDictionary<string, string> Details { get; }

    public bool Retryable { get; }
}
