using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using BDTool.Contracts.Formation;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Projects;
using BDTool.Contracts.Workspace;
using BDTool.Operations.Diagnostics;

namespace BDTool.Cli;

/// <summary>--human 模式的人类可读输出；机器协议仍由 CliOutput 负责。</summary>
internal static class CliHumanFormatter
{
    public static void WriteResult(OperationResult result, TextWriter stdout, TextWriter stderr)
    {
        stdout.WriteLine("status: " + JsonNamingPolicy.CamelCase.ConvertName(result.Status.ToString()));
        switch (result)
        {
            case OperationResult<DoctorReport> { Data: not null } doctor:
                stdout.WriteLine("state: " + doctor.Data.State);
                stdout.WriteLine("project: " + doctor.Data.ProjectPath);
                stdout.WriteLine("providers: " + string.Join(",", doctor.Data.Providers.Select(provider => provider.Id)));
                stdout.WriteLine("unity.connected: " + doctor.Data.Unity.Connected.ToString().ToLowerInvariant());
                break;
            case OperationResult<OperationsListData> { Data: not null } list:
                foreach (var operation in list.Data.Operations)
                {
                    stdout.WriteLine($"{operation.Id}\t{operation.Availability}\t{operation.Description}");
                }

                break;
            case OperationResult<OperationsDescribeData> { Data: not null } describe:
                stdout.WriteLine("operation: " + describe.Data.Operation.Id);
                stdout.WriteLine("effect: " + describe.Data.Operation.Effect);
                stdout.WriteLine("availability: " + describe.Data.Operation.Availability);
                break;
            case OperationResult<ProjectContextData> { Data: not null } context:
                stdout.WriteLine("project: " + context.Data.ProjectPath);
                stdout.WriteLine("unityVersion: " + (context.Data.UnityVersion ?? "-"));
                stdout.WriteLine("editor.connected: " + context.Data.Editor.Connected.ToString().ToLowerInvariant());
                stdout.WriteLine("providers: " + string.Join(",", context.Data.Providers.Select(provider => provider.Id)));
                break;
            case OperationResult<WorkspaceReadResult> { Data: not null } read:
                stdout.WriteLine("path: " + read.Data.RelativePath);
                stdout.WriteLine("sha256: " + read.Data.Sha256);
                stdout.WriteLine("length: " + read.Data.Length.ToString(CultureInfo.InvariantCulture));
                stdout.WriteLine(read.Data.Content);
                break;
            case OperationResult<WorkspaceSearchResult> { Data: not null } search:
                stdout.WriteLine("matches: " + search.Data.Matches.Count.ToString(CultureInfo.InvariantCulture));
                stdout.WriteLine("truncated: " + search.Data.Truncated.ToString().ToLowerInvariant());
                foreach (var match in search.Data.Matches)
                {
                    stdout.WriteLine($"{match.RelativePath}:{match.LineNumber}: {match.Line}");
                }

                break;
            case OperationResult<WorkspaceFingerprintResult> { Data: not null } fingerprint:
                foreach (var file in fingerprint.Data.Files)
                {
                    stdout.WriteLine($"{file.RelativePath}\texists={file.Exists.ToString().ToLowerInvariant()}\t{file.Sha256}");
                }

                break;
            case OperationResult<WorkspaceChangeResult> { Data: not null } change:
                stdout.WriteLine("path: " + change.Data.RelativePath);
                stdout.WriteLine("changed: " + change.Data.Changed.ToString().ToLowerInvariant());
                stdout.WriteLine("created: " + change.Data.Created.ToString().ToLowerInvariant());
                stdout.WriteLine("dryRun: " + change.Data.DryRun.ToString().ToLowerInvariant());
                stdout.WriteLine("sha256: " + change.Data.NewSha256);
                break;
            case OperationResult<FormationCommandData> { Data: not null } command:
                stdout.WriteLine("requestId: " + command.Data.RequestId);
                stdout.WriteLine("replayed: " + command.Data.Replayed.ToString().ToLowerInvariant());
                WriteFormation(command.Data.Formation, stdout);
                break;
            case OperationResult<FormationData> { Data: not null } formation:
                WriteFormation(formation.Data, stdout);
                break;
            case OperationResult<FormationListData> { Data: not null } formations:
                foreach (var item in formations.Data.Formations)
                {
                    stdout.WriteLine(
                        $"{item.Id}\t{item.Status}\t{item.PassedTaskCount}/{item.TaskCount}\t{item.Objective}");
                }

                break;
        }

        foreach (var error in result.Errors)
        {
            stderr.WriteLine($"bdtool: {error.Code} ({error.Stage}) {error.Message}");
        }
    }

    private static void WriteFormation(FormationData formation, TextWriter stdout)
    {
        stdout.WriteLine("formation: " + formation.Id);
        stdout.WriteLine("status: " + formation.Status);
        stdout.WriteLine("requirementVersion: " + formation.RequirementVersion.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("objective: " + formation.Objective);
        if (!string.IsNullOrWhiteSpace(formation.BlockedReason))
        {
            stdout.WriteLine("blockedReason: " + formation.BlockedReason);
        }

        foreach (var seat in formation.Seats)
        {
            stdout.WriteLine(
                $"seat: {seat.Id}\t{seat.Role}\t{seat.Workspace.Access}\tactiveTask={seat.ActiveTaskId}");
        }

        foreach (var task in formation.Tasks)
        {
            stdout.WriteLine(
                $"task: {task.Id}\t{task.State}\tattempts={task.Attempts.Count}\t" +
                $"submissions={task.Submissions.Count}\treviews={task.Reviews.Count}");
        }
    }
}
