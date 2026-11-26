using System;

namespace BDTool.UnityCore
{
    /// <summary>
    /// Unity Core 对同步调用方暴露的错误分类。调用方可以只依赖错误码，
    /// 不需要解析异常文本或了解 Unity Editor 内部异常类型。
    /// </summary>
    public enum UnityCoreErrorCode
    {
        InvalidArgument,
        AssetNotFound,
        NotPrefab,
        PreconditionFailed,
        InvalidAsset,
        WriteFailed
    }

    /// <summary>
    /// 服务边界上的统一异常。消息用于诊断，稳定的程序化判断应读取 <see cref="Code"/>。
    /// </summary>
    public sealed class UnityCoreException : Exception
    {
        public UnityCoreException(UnityCoreErrorCode code, string message)
            : base(message)
        {
            Code = code;
        }

        public UnityCoreException(UnityCoreErrorCode code, string message, Exception innerException)
            : base(message, innerException)
        {
            Code = code;
        }

        public UnityCoreErrorCode Code { get; }
    }
}
