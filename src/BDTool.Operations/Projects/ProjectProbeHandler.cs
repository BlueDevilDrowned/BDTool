using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Projects;
using BDTool.Operations.Handlers;
using BDTool.Operations.Input;

namespace BDTool.Operations.Projects;

/// <summary>
/// project.probe：通过 Editor Provider 调用自定义只读工具 `bdtool_read_only_probe`，
/// 把上游输出收敛为 ProjectProbeData。输入只允许空对象；失败、取消和超时必须
/// 保持失败语义，不能因为输出可解析或字段缺失就用默认值伪造成 Completed。
/// </summary>
public sealed class ProjectProbeHandler : IOperationHandler
{
    public const string StableOperationId = "project.probe";

    public string OperationId => StableOperationId;

    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        IOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (!TryValidateInput(request.InputJson, out var inputError))
        {
            return OperationResults.Failure<ProjectProbeData>(
                ErrorCodes.InvalidArgument,
                "input",
                inputError);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return OperationResults.Failure<ProjectProbeData>(
                ErrorCodes.Cancelled,
                "editor",
                "project.probe 已取消。",
                OperationStatus.Cancelled);
        }

        var timeoutSeconds = request.TimeoutSeconds <= 0 ? 60 : request.TimeoutSeconds;
        OperationHandle? handle = null;
        try
        {
            handle = await context.Editor.SubmitAsync(
                new EditorOperationRequest
                {
                    ProjectPath = request.ProjectPath,
                    OperationId = StableOperationId,
                    // 严格空对象输入规范化为 "{}"，不转发调用方原始字符串。
                    InputJson = "{}",
                    WriteScope = request.WriteScope,
                    ExpectedFingerprint = request.ExpectedFingerprint,
                    TimeoutSeconds = timeoutSeconds,
                    DryRun = request.DryRun
                },
                cancellationToken).ConfigureAwait(false);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await context.Editor.GetResultAsync(handle, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Status is not OperationStatus.Queued
                    and not OperationStatus.Running)
                {
                    return MapResult(result);
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    await TryCancelAsync(context.Editor, handle).ConfigureAwait(false);
                    return OperationResults.Failure<ProjectProbeData>(
                        ErrorCodes.Timeout,
                        "editor",
                        "project.probe 超时。");
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (handle is not null)
            {
                await TryCancelAsync(context.Editor, handle).ConfigureAwait(false);
            }

            return OperationResults.Failure<ProjectProbeData>(
                ErrorCodes.Cancelled,
                "editor",
                "project.probe 已取消。",
                OperationStatus.Cancelled);
        }
        catch (EditorException exception)
        {
            return OperationResults.Failure<ProjectProbeData>(
                exception.Code,
                "editor",
                exception.Message,
                details: new Dictionary<string, string>(
                    exception.Details,
                    StringComparer.Ordinal));
        }
        catch (NotSupportedException)
        {
            return OperationResults.Failure<ProjectProbeData>(
                ErrorCodes.CapabilityUnsupported,
                "editor",
                "当前运行时未装配 Editor Provider。");
        }
    }

    /// <summary>
    /// 输入必须严格是空 JSON 对象；任何字段都会被 ParseObject 以未知字段拒绝，
    /// 数组、标量、注释、尾逗号和重复字段同样拒绝。
    /// </summary>
    private static bool TryValidateInput(string? inputJson, out string error)
    {
        try
        {
            using var document = OperationInputParser.ParseObject(inputJson);
            error = string.Empty;
            return true;
        }
        catch (OperationInputException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static OperationResult MapResult(EditorOperationResult result)
    {
        if (result.Status == OperationStatus.Cancelled)
        {
            return OperationResults.Failure<ProjectProbeData>(
                ErrorCodes.Cancelled,
                "editor",
                "project.probe 已取消。",
                OperationStatus.Cancelled);
        }

        if (result.Status != OperationStatus.Completed)
        {
            var first = result.Errors.Count > 0 ? result.Errors[0] : null;
            return OperationResults.Failure<ProjectProbeData>(
                first?.Code ?? ErrorCodes.ExecutionFailed,
                "editor",
                first?.Message ?? "Editor 未返回完成结果。",
                details: first?.Details);
        }

        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(result.OutputJson) ? "{}" : result.OutputJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return InvalidOutput("结果根节点必须是 JSON 对象。");
            }

            // 逐字段严格校验：Completed 但缺字段或类型错误时返回失败，
            // 不使用默认值伪造成一次成功的探针调用。
            if (!TryReadString(root, "product", out var product, out var error))
            {
                return InvalidOutput(error);
            }

            if (!TryReadString(root, "unityVersion", out var unityVersion, out error))
            {
                return InvalidOutput(error);
            }

            if (!TryReadString(root, "platform", out var platform, out error))
            {
                return InvalidOutput(error);
            }

            if (!TryReadString(root, "sceneName", out var sceneName, out error))
            {
                return InvalidOutput(error);
            }

            if (!TryReadString(root, "scenePath", out var scenePath, out error))
            {
                return InvalidOutput(error);
            }

            if (!TryReadBoolean(root, "sceneIsDirty", out var sceneIsDirty, out error))
            {
                return InvalidOutput(error);
            }

            if (!TryReadInt32(root, "rootObjectCount", out var rootObjectCount, out error))
            {
                return InvalidOutput(error);
            }

            if (!TryReadString(root, "observedAtUtc", out var observedAtUtc, out error))
            {
                return InvalidOutput(error);
            }

            return new OperationResult<ProjectProbeData>
            {
                Status = OperationStatus.Completed,
                Data = new ProjectProbeData
                {
                    Product = product,
                    UnityVersion = unityVersion,
                    Platform = platform,
                    SceneName = sceneName,
                    ScenePath = scenePath,
                    SceneIsDirty = sceneIsDirty,
                    RootObjectCount = rootObjectCount,
                    ObservedAtUtc = observedAtUtc
                }
            };
        }
        catch (JsonException)
        {
            return InvalidOutput("Editor 返回的 typed 结果不是合法 JSON。");
        }
    }

    private static OperationResult InvalidOutput(string message) =>
        OperationResults.Failure<ProjectProbeData>(
            ErrorCodes.ExecutionFailed,
            "editor",
            message);

    private static bool TryReadString(
        JsonElement root,
        string name,
        out string value,
        out string error)
    {
        if (root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            error = string.Empty;
            return true;
        }

        value = string.Empty;
        error = $"Editor 返回的结果缺少字段或类型错误：{name}。";
        return false;
    }

    private static bool TryReadBoolean(
        JsonElement root,
        string name,
        out bool value,
        out string error)
    {
        if (root.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            error = string.Empty;
            return true;
        }

        value = false;
        error = $"Editor 返回的结果缺少字段或类型错误：{name}。";
        return false;
    }

    private static bool TryReadInt32(
        JsonElement root,
        string name,
        out int value,
        out string error)
    {
        if (root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value))
        {
            error = string.Empty;
            return true;
        }

        value = 0;
        error = $"Editor 返回的结果缺少字段或类型错误：{name}。";
        return false;
    }

    private static async Task TryCancelAsync(
        IEditorKernel editor,
        OperationHandle handle)
    {
        try
        {
            await editor.CancelAsync(handle, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 取消是尽力而为；清理异常不能覆盖原始的取消/超时结果。
        }
    }
}
