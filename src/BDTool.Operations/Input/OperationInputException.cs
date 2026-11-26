using System;

namespace BDTool.Operations.Input;

/// <summary>操作输入的格式错误。</summary>
public sealed class OperationInputException : Exception
{
    public OperationInputException(string message) : base(message) { }
    public OperationInputException(string message, Exception innerException) : base(message, innerException) { }
}
