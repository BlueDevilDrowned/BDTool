using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BDTool.Cli;

/// <summary>
/// 把 --input 文件或 --input-json 内联文本变成 OperationRequest.InputJson。
/// 文件必须按严格 UTF-8 读取且内容必须是 JSON 对象；业务字段仍由 Handler 校验。
/// </summary>
internal static class CliInputLoader
{
    public static (string? Json, string? Error) Load(CliOptions options)
    {
        string text;
        if (!string.IsNullOrWhiteSpace(options.InputFile))
        {
            try
            {
                text = File.ReadAllText(options.InputFile, new UTF8Encoding(false, true));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                return (null, $"无法读取 --input 文件 {options.InputFile}：{exception.Message}");
            }
        }
        else
        {
            text = options.InputJson ?? "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "操作输入必须是 JSON 对象。");
            }
        }
        catch (JsonException exception)
        {
            return (null, "操作输入不是合法 JSON：" + exception.Message);
        }

        return (text, null);
    }
}
