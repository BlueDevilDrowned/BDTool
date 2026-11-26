using System;

namespace BDTool.Operations.Catalog;

/// <summary>
/// 操作目录不可用或不合法。消息面向调用方，必须给出具体字段与原因，
/// 不包含内部堆栈语义。
/// </summary>
public sealed class OperationCatalogException : Exception
{
    public OperationCatalogException(string message)
        : base(message)
    {
    }

    public OperationCatalogException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
