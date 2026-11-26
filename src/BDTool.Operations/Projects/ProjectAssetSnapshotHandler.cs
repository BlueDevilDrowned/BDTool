using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
/// project.asset-snapshot：通过 Editor Provider 调用只读工具 `bdtool_core_read`，
/// 把上游快照收敛为 ProjectAssetSnapshotData。输入只允许必填非空 assetPath；
/// 路径是否合法、资产是否存在由 Unity Core 判定，Handler 不直接访问文件系统或 Unity。
/// Completed 但字段缺失或类型错误时保持失败语义，不能用默认值伪造成成功快照。
/// </summary>
public sealed class ProjectAssetSnapshotHandler : IOperationHandler
{
    public const string StableOperationId = "project.asset-snapshot";

    /// <summary>上游 bdtool_core_read 接受的唯一输入字段。</summary>
    private static readonly string[] s_allowedInputProperties = { "assetPath" };

    public string OperationId => StableOperationId;

    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        IOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (!TryPrepareInput(request.InputJson, out var normalizedInput, out var error))
        {
            return OperationResults.Failure<ProjectAssetSnapshotData>(
                ErrorCodes.InvalidArgument,
                "input",
                error);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return OperationResults.Failure<ProjectAssetSnapshotData>(
                ErrorCodes.Cancelled,
                "editor",
                "project.asset-snapshot 已取消。",
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
                    // 只转发白名单字段，避免把调用方原始 JSON 中的未知内容带入 Provider。
                    InputJson = normalizedInput,
                    // 只读操作显式声明空写范围，Provider 会校验 Mutating 与操作声明一致。
                    WriteScope = new List<string>(),
                    ExpectedFingerprint = null,
                    TimeoutSeconds = timeoutSeconds,
                    DryRun = false,
                    Mutating = false
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
                    return MapResult(request, result);
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    await TryCancelAsync(context.Editor, handle).ConfigureAwait(false);
                    return OperationResults.Failure<ProjectAssetSnapshotData>(
                        ErrorCodes.Timeout,
                        "editor",
                        "project.asset-snapshot 超时。");
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

            return OperationResults.Failure<ProjectAssetSnapshotData>(
                ErrorCodes.Cancelled,
                "editor",
                "project.asset-snapshot 已取消。",
                OperationStatus.Cancelled);
        }
        catch (EditorException exception)
        {
            return OperationResults.Failure<ProjectAssetSnapshotData>(
                exception.Code,
                "editor",
                exception.Message,
                details: new Dictionary<string, string>(
                    exception.Details,
                    StringComparer.Ordinal));
        }
        catch (NotSupportedException)
        {
            return OperationResults.Failure<ProjectAssetSnapshotData>(
                ErrorCodes.CapabilityUnsupported,
                "editor",
                "当前运行时未装配 Editor Provider。");
        }
    }

    /// <summary>
    /// 输入必须严格为 {assetPath}；未知字段、重复字段、注释、尾逗号和非对象输入
    /// 都会在 Submit 之前被拒绝，避免先占用 Editor Job 再失败。
    /// </summary>
    private static bool TryPrepareInput(
        string? inputJson,
        out string normalizedInput,
        out string error)
    {
        normalizedInput = "{}";
        try
        {
            using var document = OperationInputParser.ParseObject(
                inputJson,
                s_allowedInputProperties);
            var assetPath = OperationInputParser.RequireString(
                document.RootElement,
                "assetPath");
            normalizedInput = BuildNormalizedInput(assetPath);
            error = string.Empty;
            return true;
        }
        catch (OperationInputException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static string BuildNormalizedInput(string assetPath)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("assetPath", assetPath);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static OperationResult MapResult(
        OperationRequest request,
        EditorOperationResult result)
    {
        if (result.Status == OperationStatus.Cancelled)
        {
            return OperationResults.Failure<ProjectAssetSnapshotData>(
                ErrorCodes.Cancelled,
                "editor",
                "project.asset-snapshot 已取消。",
                OperationStatus.Cancelled);
        }

        if (result.Status != OperationStatus.Completed)
        {
            var first = result.Errors.Count > 0 ? result.Errors[0] : null;
            return OperationResults.Failure<ProjectAssetSnapshotData>(
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

            if (!TryReadString(root, "assetPath", out var assetPath, out var error))
            {
                return InvalidOutput(error);
            }

            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return InvalidOutput("Editor 返回的 assetPath 不能为空。");
            }

            if (!TryReadBoolean(root, "exists", out var exists, out error)
                || !TryReadString(root, "sha256", out var sha256, out error)
                || !TryReadString(root, "rootObjectName", out var rootObjectName, out error)
                || !TryReadInt32(root, "childCount", out var childCount, out error)
                || !TryReadString(root, "unityVersion", out var unityVersion, out error))
            {
                return InvalidOutput(error);
            }

            // 存在资产却没有哈希说明上游快照不完整，不能当作成功返回。
            if (exists && string.IsNullOrWhiteSpace(sha256))
            {
                return InvalidOutput("资产存在时 Editor 必须返回非空 sha256。");
            }

            return new OperationResult<ProjectAssetSnapshotData>
            {
                Status = OperationStatus.Completed,
                Data = new ProjectAssetSnapshotData
                {
                    ProjectPath = request.ProjectPath,
                    AssetPath = assetPath,
                    Exists = exists,
                    Sha256 = sha256,
                    RootObjectName = rootObjectName,
                    ChildCount = childCount,
                    UnityVersion = unityVersion
                }
            };
        }
        catch (JsonException)
        {
            return InvalidOutput("Editor 返回的 typed 结果不是合法 JSON。");
        }
    }

    private static OperationResult InvalidOutput(string message) =>
        OperationResults.Failure<ProjectAssetSnapshotData>(
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
