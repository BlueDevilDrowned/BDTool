using System;

namespace BDTool.Cli;

/// <summary>CLI 一次调用的解析结果。Json 为默认模式，Human 仅由 --human 显式开启。</summary>
internal sealed class CliOptions
{
    public bool Json { get; set; } = true;
    public bool Human { get; set; }
    public bool DryRun { get; set; }
    public string ProjectPath { get; set; } = string.Empty;
    public string? CatalogPath { get; set; }
    public string? InputFile { get; set; }
    public string? InputJson { get; set; }
}

/// <summary>解析选项时保留“是否出现过”，用于拒绝重复关键选项而不是静默覆盖。</summary>
internal sealed class OptionParseResult
{
    public CliOptions? Options { get; init; }
    public string? Error { get; init; }

    public static OptionParseResult Success(CliOptions options) => new() { Options = options };
    public static OptionParseResult Failure(string error) => new() { Error = error };
}

/// <summary>
/// CLI 选项解析器。只做语法与组合校验；输入 JSON 的 UTF-8/JSON 合法性由
/// CliInputLoader 处理，业务字段由 Operations Handler 严格校验。
/// </summary>
internal static class CliOptionParser
{
    public static OptionParseResult Parse(string[] args, int startIndex, bool allowOperationOptions)
    {
        var options = new CliOptions();
        var projectSeen = false;
        var catalogSeen = false;
        var inputSeen = false;
        var inputJsonSeen = false;
        var dryRunSeen = false;
        var humanSeen = false;
        var jsonSeen = false;

        for (var index = startIndex; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--json":
                    // 默认即 JSON；重复 --json 保持幂等。
                    jsonSeen = true;
                    options.Json = true;
                    break;
                case "--human":
                    if (humanSeen)
                    {
                        return OptionParseResult.Failure("重复选项 --human。");
                    }

                    humanSeen = true;
                    options.Human = true;
                    options.Json = false;
                    break;
                case "--project":
                    if (projectSeen)
                    {
                        return OptionParseResult.Failure("重复选项 --project。");
                    }

                    if (!TryReadValue(args, ref index, "--project", out var projectPath))
                    {
                        return OptionParseResult.Failure("--project 需要一个路径。");
                    }

                    projectSeen = true;
                    options.ProjectPath = projectPath;
                    break;
                case "--catalog":
                    if (catalogSeen)
                    {
                        return OptionParseResult.Failure("重复选项 --catalog。");
                    }

                    if (!TryReadValue(args, ref index, "--catalog", out var catalogPath))
                    {
                        return OptionParseResult.Failure("--catalog 需要一个文件路径。");
                    }

                    catalogSeen = true;
                    options.CatalogPath = catalogPath;
                    break;
                case "--input" when allowOperationOptions:
                    if (inputSeen || inputJsonSeen)
                    {
                        return OptionParseResult.Failure("--input 与 --input-json 只能出现一次且不能同时使用。");
                    }

                    if (!TryReadValue(args, ref index, "--input", out var inputFile))
                    {
                        return OptionParseResult.Failure("--input 需要一个文件路径。");
                    }

                    inputSeen = true;
                    options.InputFile = inputFile;
                    break;
                case "--input-json" when allowOperationOptions:
                    if (inputJsonSeen || inputSeen)
                    {
                        return OptionParseResult.Failure("--input 与 --input-json 只能出现一次且不能同时使用。");
                    }

                    if (!TryReadValue(args, ref index, "--input-json", out var inputJson))
                    {
                        return OptionParseResult.Failure("--input-json 需要一个内联 JSON 对象。");
                    }

                    inputJsonSeen = true;
                    options.InputJson = inputJson;
                    break;
                case "--dry-run" when allowOperationOptions:
                    if (dryRunSeen)
                    {
                        return OptionParseResult.Failure("重复选项 --dry-run。");
                    }

                    dryRunSeen = true;
                    options.DryRun = true;
                    break;
                default:
                    return OptionParseResult.Failure($"未知选项 {argument}。");
            }
        }

        if (humanSeen && jsonSeen)
        {
            return OptionParseResult.Failure("--json 与 --human 不能同时使用。");
        }

        return OptionParseResult.Success(options);
    }

    /// <summary>读取选项值并拒绝把下一个选项当作值，避免命令静默错位。</summary>
    private static bool TryReadValue(string[] args, ref int index, string option, out string value)
    {
        value = string.Empty;
        if (index + 1 >= args.Length)
        {
            return false;
        }

        var candidate = args[index + 1];
        if (candidate.StartsWith('-'))
        {
            return false;
        }

        value = candidate;
        index++;
        return true;
    }
}
