using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Application;
using BDTool.Composition;
using BDTool.Contracts;
using BDTool.Contracts.Activity;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Gateway;
using BDTool.Contracts.Operations;
using BDTool.Core;
using BDTool.Operations.Catalog;

namespace BDTool.Cli;

/// <summary>
/// 可测试的 CLI 入口。参数解析、组合、执行与退出码映射都在这里，
/// Main 只负责把控制台流传进来，方便测试直接调用而不启动子进程。
/// </summary>
public static class CliEntrypoint
{
    /// <summary>activity list 未显式指定数量时的返回上限。</summary>
    private const int DefaultActivityListLimit = 100;

    /// <summary>运行一次 CLI 调用并返回稳定退出码。</summary>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length == 0)
        {
            WriteUsage(stderr);
            return (int)CliExitCode.InvalidArgument;
        }

        var command = args[0];
        if (IsHelp(command))
        {
            WriteUsage(stdout);
            return (int)CliExitCode.Success;
        }

        if (IsVersion(command))
        {
            stdout.WriteLine(
                $"{BDToolIdentity.ExecutableName} {BDToolIdentity.Version} " +
                $"(contract {BDToolContract.Version}, milestone {BDToolIdentity.Milestone})");
            return (int)CliExitCode.Success;
        }

        if (string.Equals(command, "activity", StringComparison.Ordinal))
        {
            return await RunActivityCommandAsync(args, stdout, stderr, cancellationToken).ConfigureAwait(false);
        }

        if (command.StartsWith('-'))
        {
            stderr.WriteLine($"bdtool: 未知选项 {command}");
            WriteUsage(stderr);
            return (int)CliExitCode.InvalidArgument;
        }

        var parse = ParseOptions(args, 1, allowOperationOptions: true);
        if (parse.Error is not null)
        {
            stderr.WriteLine($"bdtool: {parse.Error}");
            return (int)CliExitCode.InvalidArgument;
        }

        var options = parse.Options!;
        var operationId = ResolveOperationId(command);
        var runtimeResult = TryCreateRuntime(options);
        if (runtimeResult.Failure is not null)
        {
            WriteResult(runtimeResult.Failure, options.Json, stdout, stderr);
            return (int)ExitCodePolicy.Map(runtimeResult.Failure);
        }

        var runtime = runtimeResult.Runtime!;
        var activityId = Guid.NewGuid().ToString("n");
        var request = new OperationRequest
        {
            OperationId = operationId,
            ProjectPath = runtime.ProjectPath,
            InputJson = options.InputJson,
            DryRun = options.DryRun
        };
        var activity = new ActivityContext
        {
            ActivityId = activityId,
            TraceId = activityId,
            Source = ActivitySources.Cli,
            Actor = new ActivityActor
            {
                Type = ActorTypes.Ai,
                AgentId = string.Empty,
                SessionId = string.Empty
            }
        };

        var result = await runtime.Gateway
            .ExecuteAsync(request, activity, cancellationToken)
            .ConfigureAwait(false);
        WriteResult(result, options.Json, stdout, stderr);
        return (int)ExitCodePolicy.Map(result);
    }

    /// <summary>
    /// activity 命令是 CLI 专用查询路由，不映射到冻结目录中的操作 ID：
    /// `activity list` 输出摘要列表，`activity show` 输出单条完整记录。
    /// </summary>
    private static async Task<int> RunActivityCommandAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var subcommand = args.Length > 1 ? args[1] : string.Empty;
        if (!string.Equals(subcommand, "list", StringComparison.Ordinal)
            && !string.Equals(subcommand, "show", StringComparison.Ordinal))
        {
            stderr.WriteLine("bdtool: activity 需要 list 或 show 子命令。");
            WriteUsage(stderr);
            return (int)CliExitCode.InvalidArgument;
        }

        string? activityId = null;
        var optionStart = 2;
        if (string.Equals(subcommand, "show", StringComparison.Ordinal))
        {
            if (args.Length <= 2 || args[2].StartsWith('-'))
            {
                stderr.WriteLine("bdtool: activity show 需要一个 activityId。");
                return (int)CliExitCode.InvalidArgument;
            }

            activityId = args[2];
            optionStart = 3;
        }

        var parse = ParseOptions(args, optionStart, allowOperationOptions: false);
        if (parse.Error is not null)
        {
            stderr.WriteLine($"bdtool: {parse.Error}");
            return (int)CliExitCode.InvalidArgument;
        }

        var options = parse.Options!;
        var runtimeResult = TryCreateRuntime(options);
        if (runtimeResult.Failure is not null)
        {
            WriteResult(runtimeResult.Failure, options.Json, stdout, stderr);
            return (int)ExitCodePolicy.Map(runtimeResult.Failure);
        }

        var runtime = runtimeResult.Runtime!;
        try
        {
            if (string.Equals(subcommand, "list", StringComparison.Ordinal))
            {
                var summaries = await runtime.ActivityQuery
                    .QueryAsync(new ActivityQuery { Limit = DefaultActivityListLimit }, cancellationToken)
                    .ConfigureAwait(false);
                WriteActivityList(summaries, options.Json, stdout);
                return (int)CliExitCode.Success;
            }

            var record = await runtime.ActivityQuery
                .GetAsync(activityId!, cancellationToken)
                .ConfigureAwait(false);
            WriteActivityRecord(record, options.Json, stdout);
            return (int)CliExitCode.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = Failure(ErrorCodes.Cancelled, "activity", "查询已取消。", OperationStatus.Cancelled);
            WriteResult(cancelled, options.Json, stdout, stderr);
            return (int)ExitCodePolicy.Map(cancelled);
        }
        catch (FileNotFoundException)
        {
            var missing = Failure(
                ErrorCodes.NotFound,
                "activity",
                "找不到 Activity：" + activityId + "。",
                OperationStatus.Failed,
                new Dictionary<string, string> { ["activityId"] = activityId ?? string.Empty });
            WriteResult(missing, options.Json, stdout, stderr);
            return (int)ExitCodePolicy.Map(missing);
        }
        catch (ArgumentException exception)
        {
            var invalid = Failure(ErrorCodes.InvalidArgument, "activity", exception.Message);
            WriteResult(invalid, options.Json, stdout, stderr);
            return (int)ExitCodePolicy.Map(invalid);
        }
        catch (Exception exception)
        {
            var failed = Failure(
                ErrorCodes.ExecutionFailed,
                "activity",
                "Activity 查询失败。",
                OperationStatus.Failed,
                new Dictionary<string, string> { ["exceptionType"] = exception.GetType().Name });
            WriteResult(failed, options.Json, stdout, stderr);
            return (int)ExitCodePolicy.Map(failed);
        }
    }

    /// <summary>按选项创建运行时；目录或装配错误转换成协议结果而不是抛给进程。</summary>
    private static (BDToolRuntime? Runtime, OperationResult? Failure) TryCreateRuntime(CliOptions options)
    {
        try
        {
            var runtime = BDToolComposition.Create(new BDToolCompositionOptions
            {
                ProjectPath = options.ProjectPath,
                CatalogPath = options.CatalogPath
            });
            return (runtime, null);
        }
        catch (OperationCatalogException exception)
        {
            return (null, Failure(ErrorCodes.EnvironmentUnsupported, "catalog", exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            // 组合一致性校验失败属于构建缺陷，但对 CLI 调用方仍应返回稳定协议错误。
            return (null, Failure(ErrorCodes.EnvironmentUnsupported, "composition", exception.Message));
        }
    }

    private static void WriteActivityList(
        IReadOnlyList<ActivitySummary> summaries,
        bool json,
        TextWriter stdout)
    {
        if (json)
        {
            stdout.WriteLine(CliOutput.Serialize(new ActivityListOutput
            {
                Items = summaries.ToList(),
                Count = summaries.Count
            }));
            return;
        }

        if (summaries.Count == 0)
        {
            stdout.WriteLine("bdtool: 没有 Activity 记录。");
            return;
        }

        foreach (var summary in summaries)
        {
            stdout.WriteLine(
                $"{summary.ActivityId}  {summary.Status}  {summary.OperationId}  " +
                $"{summary.StartedAt:yyyy-MM-ddTHH:mm:ssZ}");
        }
    }

    private static void WriteActivityRecord(ActivityRecord record, bool json, TextWriter stdout)
    {
        if (json)
        {
            stdout.WriteLine(CliOutput.Serialize(record));
            return;
        }

        stdout.WriteLine($"activityId: {record.ActivityId}");
        stdout.WriteLine($"traceId: {record.TraceId}");
        stdout.WriteLine($"source: {record.Source}");
        stdout.WriteLine($"operationId: {record.OperationId}");
        stdout.WriteLine($"status: {record.Status}");
        stdout.WriteLine($"exitCode: {record.ExitCode?.ToString() ?? "-"}");
        stdout.WriteLine($"startedAt: {record.StartedAt:O}");
        stdout.WriteLine($"finishedAt: {record.FinishedAt?.ToString("O") ?? "-"}");
        if (record.Error is not null)
        {
            stdout.WriteLine($"error: {record.Error.Code} {record.Error.Message}");
        }
    }

    private static void WriteResult(OperationResult result, bool json, TextWriter stdout, TextWriter stderr)
    {
        if (json)
        {
            stdout.WriteLine(CliOutput.SerializeResult(result));
            return;
        }

        if (result.Errors.Count == 0)
        {
            stdout.WriteLine($"bdtool: {result.Status}");
            return;
        }

        foreach (var error in result.Errors)
        {
            stderr.WriteLine($"bdtool: {error.Code} ({error.Stage}) {error.Message}");
        }
    }

    private static OperationResult Failure(
        string code,
        string stage,
        string message,
        OperationStatus status = OperationStatus.Failed,
        Dictionary<string, string>? details = null)
    {
        var result = new OperationResult { Status = status };
        result.Errors.Add(new KernelError
        {
            Code = code,
            Stage = stage,
            Message = message,
            Retryable = false,
            Details = details ?? new Dictionary<string, string>()
        });
        return result;
    }

    private static bool IsHelp(string command) => command is "--help" or "-h" or "help";

    private static bool IsVersion(string command) => command is "--version" or "-v" or "version";

    /// <summary>把用户友好的短命令映射到目录中的稳定操作 ID。</summary>
    private static string ResolveOperationId(string command) => command switch
    {
        "doctor" => "system.doctor",
        _ => command
    };

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine($"{BDToolIdentity.ExecutableName} {BDToolIdentity.Version}（{BDToolIdentity.ProductName} {BDToolIdentity.Milestone}）");
        writer.WriteLine();
        writer.WriteLine("用法:");
        writer.WriteLine("  bdtool doctor [--json] [--project <path>]");
        writer.WriteLine("  bdtool activity list [--json] [--project <path>]");
        writer.WriteLine("  bdtool activity show <activityId> [--json] [--project <path>]");
        writer.WriteLine("  bdtool <operation> [--input <json>] [--json] [--dry-run] [--project <path>]");
        writer.WriteLine("  bdtool --version | --help");
        writer.WriteLine();
        writer.WriteLine("M0 限制:");
        writer.WriteLine("  仅 system.doctor 已实现；其余目录操作返回 CAPABILITY_UNSUPPORTED（退出码 3）。");
        writer.WriteLine("  不连接 Unity，Provider 列表为空。");
        writer.WriteLine();
        writer.WriteLine("退出码:");
        writer.WriteLine("  0 成功  2 参数错误  3 能力不支持  4 环境未就绪  5 执行失败");
        writer.WriteLine("  6 超时  7 取消  8 结果不确定");
    }

    private static (CliOptions? Options, string? Error) ParseOptions(
        string[] args,
        int startIndex,
        bool allowOperationOptions)
    {
        var options = new CliOptions();
        for (var index = startIndex; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--json":
                    options.Json = true;
                    break;
                case "--dry-run" when allowOperationOptions:
                    options.DryRun = true;
                    break;
                case "--project":
                    if (!TryReadValue(args, ref index, out var projectPath))
                    {
                        return (null, "--project 需要一个路径。");
                    }

                    options.ProjectPath = projectPath;
                    break;
                case "--input" when allowOperationOptions:
                    if (!TryReadValue(args, ref index, out var inputJson))
                    {
                        return (null, "--input 需要一个 JSON 字符串。");
                    }

                    options.InputJson = inputJson;
                    break;
                default:
                    return (null, $"未知选项 {argument}");
            }
        }

        return (options, null);
    }

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        value = string.Empty;
        if (index + 1 >= args.Length)
        {
            return false;
        }

        value = args[++index];
        return true;
    }

    private sealed class CliOptions
    {
        public bool Json { get; set; }
        public bool DryRun { get; set; }
        public string ProjectPath { get; set; } = string.Empty;
        public string? CatalogPath { get; set; }
        public string InputJson { get; set; } = "{}";
    }
}
