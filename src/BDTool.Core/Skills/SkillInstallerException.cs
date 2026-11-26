using System;

namespace BDTool.Core.Skills;

/// <summary>
/// Skill 安装的稳定失败载体。Code 使用 BDTool.Contracts.Errors.ErrorCodes 中的公共错误码，
/// 让上层无需依赖安装器内部异常类型即可把失败映射到 CLI 或操作结果。
/// </summary>
public sealed class SkillInstallerException : Exception
{
    public SkillInstallerException(string code, string message)
        : this(code, message, innerException: null)
    {
    }

    public SkillInstallerException(string code, string message, Exception? innerException)
        : base(message, innerException)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Skill 安装错误码不能为空。", nameof(code));
        }

        Code = code;
    }

    /// <summary>稳定公共错误码，例如 INVALID_ARGUMENT、NOT_FOUND。</summary>
    public string Code { get; }
}
