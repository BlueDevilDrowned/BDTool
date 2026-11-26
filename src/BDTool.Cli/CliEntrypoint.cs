using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
/// 可测试 CLI 入口。默认输出 JSON；--human 切换人类展示。所有实际操作都经
/// IApplicationGateway，因此 operations/context/workspace 路由共享能力检查与 Activity 审计。
/// </summary>
public static class CliEntrypoint
{
    private const int DefaultActivityListLimit = 100;

    /// <summary>Activity 文件无法解析时向调用方暴露的稳定告警码。</summary>
    private const string ActivityFileUnreadable = "ACTIVITY_FILE_UNREADABLE";

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        try
        {
            return await RunCoreAsync(args, stdout, stderr, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = Failure(ErrorCodes.Cancelled, "cli", "调用已取消。", OperationStatus.Cancelled);
            WriteResult(cancelled, new CliOptions(), stdout, stderr);
            return (int)ExitCodePolicy.Map(cancelled);
        }
        catch (Exception exception)
        {
            // 非法初始化路径、组合根异常等不得让进程带裸异常退出：
            // stdout 必须仍是一个合法统一信封，参数类异常映射为 INVALID_ARGUMENT。
            var invalidArgument = exception is ArgumentException;
            var failed = Failure(
                invalidArgument ? ErrorCodes.InvalidArgument : ErrorCodes.InternalError,
                "cli",
                "CLI 执行失败：" + exception.Message,
                OperationStatus.Failed,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exceptionType"] = exception.GetType().Name
                });
            WriteResult(failed, new CliOptions(), stdout, stderr);
            return (int)ExitCodePolicy.Map(failed);
        }
    }

    /// <summary>命令分发主体；异常统一由 <see cref="RunAsync"/> 边界转换为 JSON 信封。</summary>
    private static async Task<int> RunCoreAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {

        if (args.Length == 0)
        {
            WriteUsage(stderr);
            return WriteInvalidArgument("缺少命令。", new CliOptions(), stdout, stderr);
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

        if (string.Equals(command, "operations", StringComparison.Ordinal))
        {
            return await RunOperationsCommandAsync(args, stdout, stderr, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(command, "run", StringComparison.Ordinal))
        {
            if (args.Length <= 1 || args[1].StartsWith('-'))
            {
                return WriteInvalidArgument("run 需要一个 operationId。", new CliOptions(), stdout, stderr);
            }

            return await RunParsedOperationAsync(
                ResolveOperationId(args[1]),
                args,
                optionStart: 2,
                allowInput: true,
                stdout,
                stderr,
                cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(command, "doctor", StringComparison.Ordinal)
            || string.Equals(command, "context", StringComparison.Ordinal))
        {
            return await RunParsedOperationAsync(
                ResolveOperationId(command),
                args,
                optionStart: 1,
                allowInput: false,
                stdout,
                stderr,
                cancellationToken).ConfigureAwait(false);
        }

        if (command.StartsWith('-'))
        {
            return WriteInvalidArgument($"未知选项 {command}。", new CliOptions(), stdout, stderr);
        }

        // 兼容入口：bdtool <operationId> 直接执行目录操作。
        return await RunParsedOperationAsync(
            ResolveOperationId(command),
            args,
            optionStart: 1,
            allowInput: true,
            stdout,
            stderr,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>operations list/describe 必须进入 Gateway，不能由解析器直接读取目录。</summary>
    private static async Task<int> RunOperationsCommandAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var subcommand = args.Length > 1 ? args[1] : string.Empty;
        if (string.Equals(subcommand, "list", StringComparison.Ordinal))
        {
            return await RunParsedOperationAsync(
                "operations.list",
                args,
                optionStart: 2,
                allowInput: false,
                stdout,
                stderr,
                cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(subcommand, "describe", StringComparison.Ordinal))
        {
            if (args.Length <= 2 || args[2].StartsWith('-'))
            {
                return WriteInvalidArgument("operations describe 需要一个 operationId。", new CliOptions(), stdout, stderr);
            }

            var parse = CliOptionParser.Parse(args, 3, allowOperationOptions: false);
            if (parse.Error is not null)
            {
                var mode = InferOutputMode(args);
                return WriteInvalidArgument(parse.Error, mode, stdout, stderr);
            }

            var inputJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["operationId"] = args[2]
            });
            return await ExecuteAsync(
                "operations.describe",
                inputJson,
                parse.Options!,
                stdout,
                stderr,
                cancellationToken).ConfigureAwait(false);
        }

        return WriteInvalidArgument("operations 需要 list 或 describe 子命令。", new CliOptions(), stdout, stderr);
    }

    /// <summary>activity 是 CLI 查询路由，不映射到业务操作，也不写新的 Activity。</summary>
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
            return WriteInvalidArgument("activity 需要 list 或 show 子命令。", new CliOptions(), stdout, stderr);
        }

        string? activityId = null;
        var optionStart = 2;
        if (string.Equals(subcommand, "show", StringComparison.Ordinal))
        {
            if (args.Length <= 2 || args[2].StartsWith('-'))
            {
                return WriteInvalidArgument("activity show 需要一个 activityId。", new CliOptions(), stdout, stderr);
            }

            activityId = args[2];
            optionStart = 3;
        }

        var parse = CliOptionParser.Parse(args, optionStart, allowOperationOptions: false);
        if (parse.Error is not null)
        {
            return WriteInvalidArgument(parse.Error, InferOutputMode(args), stdout, stderr);
        }

        var options = parse.Options!;
        var runtimeResult = TryCreateRuntime(options);
        if (runtimeResult.Failure is not null)
        {
            WriteResult(runtimeResult.Failure, options, stdout, stderr);
            return (int)ExitCodePolicy.Map(runtimeResult.Failure);
        }

        var runtime = runtimeResult.Runtime!;
        try
        {
            if (string.Equals(subcommand, "list", StringComparison.Ordinal))
            {
                var query = new ActivityQuery { Limit = DefaultActivityListLimit };
                ActivityQueryDiagnosticsResult diagnostics;
                if (runtime.ActivityQuery is IActivityDiagnosticsQuery diagnosticsQuery)
                {
                    diagnostics = await diagnosticsQuery
                        .QueryWithDiagnosticsAsync(query, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    // 基础端口仍可用：没有诊断能力时退化为普通列表，不伪造诊断结果。
                    diagnostics = new ActivityQueryDiagnosticsResult(
                        await runtime.ActivityQuery.QueryAsync(query, cancellationToken).ConfigureAwait(false),
                        Array.Empty<ActivityFileFailure>());
                }

                WriteActivityList(diagnostics, options, stdout, stderr);
                return (int)CliExitCode.Success;
            }

            var record = await runtime.ActivityQuery
                .GetAsync(activityId!, cancellationToken)
                .ConfigureAwait(false);
            WriteActivityRecord(record, options, stdout);
            return (int)CliExitCode.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = Failure(ErrorCodes.Cancelled, "activity", "查询已取消。", OperationStatus.Cancelled);
            WriteResult(cancelled, options, stdout, stderr);
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
            WriteResult(missing, options, stdout, stderr);
            return (int)ExitCodePolicy.Map(missing);
        }
        catch (ArgumentException exception)
        {
            var invalid = Failure(ErrorCodes.InvalidArgument, "activity", exception.Message);
            WriteResult(invalid, options, stdout, stderr);
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
            WriteResult(failed, options, stdout, stderr);
            return (int)ExitCodePolicy.Map(failed);
        }
    }

    private static async Task<int> RunParsedOperationAsync(
        string operationId,
        string[] args,
        int optionStart,
        bool allowInput,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var parse = CliOptionParser.Parse(args, optionStart, allowInput);
        if (parse.Error is not null)
        {
            return WriteInvalidArgument(parse.Error, InferOutputMode(args), stdout, stderr);
        }

        var options = parse.Options!;
        string inputJson;
        if (allowInput)
        {
            var loaded = CliInputLoader.Load(options);
            if (loaded.Error is not null)
            {
                return WriteInvalidArgument(loaded.Error, options, stdout, stderr);
            }

            inputJson = loaded.Json!;
        }
        else
        {
            inputJson = "{}";
        }

        return await ExecuteAsync(
            operationId,
            inputJson,
            options,
            stdout,
            stderr,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteAsync(
        string operationId,
        string inputJson,
        CliOptions options,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var runtimeResult = TryCreateRuntime(options);
        if (runtimeResult.Failure is not null)
        {
            WriteResult(runtimeResult.Failure, options, stdout, stderr);
            return (int)ExitCodePolicy.Map(runtimeResult.Failure);
        }

        var runtime = runtimeResult.Runtime!;
        var activityId = Guid.NewGuid().ToString("n");
        var request = new OperationRequest
        {
            OperationId = operationId,
            ProjectPath = runtime.ProjectPath,
            InputJson = inputJson,
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
        WriteResult(result, options, stdout, stderr);
        return (int)ExitCodePolicy.Map(result);
    }

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
            return (null, Failure(ErrorCodes.EnvironmentUnsupported, "composition", exception.Message));
        }
        catch (BDTool.Core.Workspace.WorkspaceException exception)
        {
            return (null, Failure(exception.Code, "composition", exception.Message));
        }
    }

    private static void WriteActivityList(
        ActivityQueryDiagnosticsResult diagnostics,
        CliOptions options,
        TextWriter stdout,
        TextWriter stderr)
    {
        var summaries = diagnostics.Summaries;
        var warnings = new List<KernelWarning>(diagnostics.UnreadableFiles.Count);
        foreach (var failure in diagnostics.UnreadableFiles)
        {
            warnings.Add(new KernelWarning
            {
                Code = ActivityFileUnreadable,
                Message = "Activity 文件无法解析，已跳过。",
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["filePath"] = failure.FilePath,
                    ["reason"] = failure.Reason
                }
            });
        }

        if (!options.Human)
        {
            stdout.WriteLine(CliOutput.Serialize(new ActivityListOutput
            {
                Items = summaries.ToList(),
                Count = summaries.Count,
                UnreadableFiles = diagnostics.UnreadableFiles.ToList(),
                Warnings = warnings
            }));
            return;
        }

        // 人类模式下诊断写 stderr，保持 stdout 只包含可读列表。
        foreach (var warning in warnings)
        {
            stderr.WriteLine(
                $"bdtool: warning {warning.Code} {warning.Details["filePath"]}: {warning.Details["reason"]}");
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

    private static void WriteActivityRecord(ActivityRecord record, CliOptions options, TextWriter stdout)
    {
        if (!options.Human)
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

    private static void WriteResult(OperationResult result, CliOptions options, TextWriter stdout, TextWriter stderr)
    {
        if (options.Human)
        {
            CliHumanFormatter.WriteResult(result, stdout, stderr);
            return;
        }

        stdout.WriteLine(CliOutput.SerializeResult(result));
    }

    private static int WriteInvalidArgument(
        string message,
        CliOptions options,
        TextWriter stdout,
        TextWriter stderr)
    {
        var result = Failure(ErrorCodes.InvalidArgument, "cli", message);
        stderr.WriteLine("bdtool: " + message);
        if (options.Human)
        {
            CliHumanFormatter.WriteResult(result, stdout, stderr);
        }
        else
        {
            // 默认 JSON 模式下 stdout 必须仍是一个合法统一结果信封。
            stdout.WriteLine(CliOutput.SerializeResult(result));
        }

        return (int)CliExitCode.InvalidArgument;
    }

    /// <summary>解析失败前根据原始参数推断输出模式，避免 --human 场景被强制 JSON。</summary>
    private static CliOptions InferOutputMode(string[] args)
    {
        var hasJson = args.Contains("--json", StringComparer.Ordinal);
        var hasHuman = args.Contains("--human", StringComparer.Ordinal);
        return new CliOptions
        {
            Json = !hasHuman || hasJson,
            Human = hasHuman && !hasJson
        };
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

    private static string ResolveOperationId(string command) => command switch
    {
        "doctor" => "system.doctor",
        "context" => "project.context",
        _ => command
    };

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine($"{BDToolIdentity.ExecutableName} {BDToolIdentity.Version}（{BDToolIdentity.ProductName} {BDToolIdentity.Milestone}）");
        writer.WriteLine();
        writer.WriteLine("用法（默认 JSON；--human 切换人类输出）:");
        writer.WriteLine("  bdtool doctor [--project <path>] [--human]");
        writer.WriteLine("  bdtool context [--project <path>] [--human]");
        writer.WriteLine("  bdtool operations list [--project <path>] [--human]");
        writer.WriteLine("  bdtool operations describe <id> [--project <path>] [--human]");
        writer.WriteLine("  bdtool run <id> [--input <file> | --input-json <json>] [--dry-run] [--project <path>] [--human]");
        writer.WriteLine("  bdtool <id> [--input <file> | --input-json <json>] [--project <path>] [--human]");
        writer.WriteLine("  bdtool activity list|show <activityId> [--project <path>] [--human]");
        writer.WriteLine("  bdtool --version | --help");
        writer.WriteLine();
        writer.WriteLine("M1 范围: 本地 Workspace read/search/fingerprint/apply；不连接 Unity。");
        writer.WriteLine();
        writer.WriteLine("退出码:");
        writer.WriteLine("  0 成功  2 参数/未找到  3 能力不支持  4 项目/环境未就绪  5 执行失败");
        writer.WriteLine("  6 超时  7 取消  8 结果不确定");
    }
}
