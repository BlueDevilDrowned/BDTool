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
using BDTool.Contracts.Workspace;
using BDTool.Core.Workspace;
using BDTool.Operations.Handlers;
using BDTool.Operations.Input;

namespace BDTool.Operations.Projects;

/// <summary>
/// project.prefab-set-active：在 SHA-256 CAS 保护下通过 Editor Provider 修改单个
/// Prefab 根对象的 activeSelf，把结果收敛为 PrefabActivationData。写入对象状态而不是
/// 根对象名称，避免 Prefab 文件 stem 与结果校验耦合。
/// Submit 之前必须用 Workspace 指纹确认 expectedSha256 与磁盘一致，CAS 失败绝不触达 Editor；
/// 非 dry-run 的写操作在提交后超时、取消或无法回读验证时返回 Uncertain，
/// 不能伪装成可安全重试的普通失败。Handler 不直接访问进程、Unity 或 Provider 私有类型。
/// </summary>
public sealed class PrefabSetActiveHandler : IOperationHandler
{
    public const string StableOperationId = "project.prefab-set-active";

    /// <summary>上游 bdtool_core_prefab_set_active 接受的字段集合。</summary>
    private static readonly string[] s_allowedInputProperties =
    {
        "assetPath",
        "expectedSha256",
        "isActive",
        "dryRun"
    };

    public string OperationId => StableOperationId;

    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        IOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (!TryPrepareInput(
                request.InputJson,
                out var assetPath,
                out var expectedSha256,
                out var requestedIsActive,
                out var inputDryRun,
                out var inputError))
        {
            return OperationResults.Failure<PrefabActivationData>(
                ErrorCodes.InvalidArgument,
                "input",
                inputError);
        }

        // 请求级 dry-run 与输入级 dry-run 取并集：任一为 true 都不得写入资产。
        var effectiveDryRun = request.DryRun || inputDryRun;
        if (cancellationToken.IsCancellationRequested)
        {
            return OperationResults.Failure<PrefabActivationData>(
                ErrorCodes.Cancelled,
                "workspace",
                "project.prefab-set-active 已取消。",
                OperationStatus.Cancelled);
        }

        // CAS 在 Submit 之前完成：文件缺失或哈希不一致时不得创建 Editor Job。
        WorkspaceFileFingerprint? preFingerprint;
        try
        {
            preFingerprint = await ReadFingerprintAsync(
                    context.Workspace,
                    request.ProjectPath,
                    assetPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OperationResults.Failure<PrefabActivationData>(
                ErrorCodes.Cancelled,
                "workspace",
                "project.prefab-set-active 已取消。",
                OperationStatus.Cancelled);
        }
        catch (WorkspaceException exception)
        {
            return OperationResults.FromWorkspaceException<PrefabActivationData>(exception);
        }

        if (preFingerprint is null || !preFingerprint.Exists)
        {
            return OperationResults.Failure<PrefabActivationData>(
                ErrorCodes.PreconditionFailed,
                "workspace",
                "目标 Prefab 不存在或 Workspace 未能返回唯一指纹。");
        }

        if (!string.Equals(
                preFingerprint.Sha256,
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return OperationResults.Failure<PrefabActivationData>(
                ErrorCodes.PreconditionFailed,
                "workspace",
                "资产 SHA-256 与 expectedSha256 不一致，未提交 Editor。",
                details: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["assetPath"] = assetPath,
                    ["expectedSha256"] = expectedSha256,
                    ["actualSha256"] = preFingerprint.Sha256
                });
        }

        var normalizedInput = BuildNormalizedInput(
            assetPath,
            expectedSha256,
            requestedIsActive,
            effectiveDryRun);
        var timeoutSeconds = request.TimeoutSeconds <= 0 ? 60 : request.TimeoutSeconds;
        OperationHandle? handle = null;
        try
        {
            handle = await context.Editor.SubmitAsync(
                new EditorOperationRequest
                {
                    ProjectPath = request.ProjectPath,
                    OperationId = StableOperationId,
                    InputJson = normalizedInput,
                    WriteScope = new List<string> { assetPath },
                    ExpectedFingerprint = expectedSha256,
                    TimeoutSeconds = timeoutSeconds,
                    DryRun = effectiveDryRun,
                    Mutating = true
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
                    return await MapResultAsync(
                            request,
                            context,
                            result,
                            assetPath,
                            expectedSha256,
                            requestedIsActive,
                            effectiveDryRun,
                            preFingerprint.Sha256,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    await TryCancelAsync(context.Editor, handle).ConfigureAwait(false);
                    return effectiveDryRun
                        ? OperationResults.Failure<PrefabActivationData>(
                            ErrorCodes.Timeout,
                            "editor",
                            "project.prefab-set-active dryRun 超时；资产未被写入。")
                        : Uncertain(
                            "Editor 写入超时，资产可能已被修改；请重新读取资产快照确认状态。",
                            WriteDetails(assetPath, "write_timeout"));
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

            return effectiveDryRun
                ? OperationResults.Failure<PrefabActivationData>(
                    ErrorCodes.Cancelled,
                    "editor",
                    "project.prefab-set-active dryRun 已取消；资产未被写入。",
                    OperationStatus.Cancelled)
                : Uncertain(
                    "Editor 写入在提交后取消，无法确认资产状态；禁止自动重试。",
                    WriteDetails(assetPath, "write_cancelled"));
        }
        catch (EditorException exception)
        {
            // 已取得 Job 句柄说明写入可能已经开始；此时不能把 Provider 异常
            // 折叠成普通失败，否则调用方可能误判为“没有副作用”并重试。
            if (handle is not null && !effectiveDryRun)
            {
                var details = new Dictionary<string, string>(
                    exception.Details,
                    StringComparer.Ordinal)
                {
                    ["assetPath"] = assetPath,
                    ["reason"] = "write_submitted_editor_error",
                    ["originalCode"] = exception.Code
                };
                return Uncertain(
                    "Editor 写入提交后发生错误，无法确认资产状态；禁止自动重试。",
                    details);
            }

            return OperationResults.Failure<PrefabActivationData>(
                exception.Code,
                "editor",
                exception.Message,
                details: new Dictionary<string, string>(
                    exception.Details,
                    StringComparer.Ordinal));
        }
        catch (NotSupportedException)
        {
            return OperationResults.Failure<PrefabActivationData>(
                ErrorCodes.CapabilityUnsupported,
                "editor",
                "当前运行时未装配 Editor Provider。");
        }
    }

    /// <summary>
    /// 严格解析输入并生成规范化 JSON。assetPath/expectedSha256/isActive 必填；
    /// dryRun 可选但必须是布尔值，写出时始终显式包含，满足 Provider 的前置校验。
    /// </summary>
    private static bool TryPrepareInput(
        string? inputJson,
        out string assetPath,
        out string expectedSha256,
        out bool isActive,
        out bool dryRun,
        out string error)
    {
        assetPath = string.Empty;
        expectedSha256 = string.Empty;
        isActive = false;
        dryRun = false;
        error = string.Empty;

        try
        {
            using var document = OperationInputParser.ParseObject(
                inputJson,
                s_allowedInputProperties);
            var root = document.RootElement;
            assetPath = OperationInputParser.RequireString(root, "assetPath");
            expectedSha256 = OperationInputParser.RequireString(root, "expectedSha256");
            isActive = RequireBoolean(root, "isActive");
            dryRun = OperationInputParser.GetBoolean(root, "dryRun");
            return true;
        }
        catch (OperationInputException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// 读取必填布尔值。OperationInputParser.GetBoolean 在字段缺失时返回默认值，
    /// 不能用于 isActive：漏传字段会被静默折叠成 false 并写入错误状态。
    /// </summary>
    private static bool RequireBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            throw new OperationInputException($"字段 {propertyName} 不能缺失。");
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new OperationInputException($"字段 {propertyName} 必须是布尔值。")
        };
    }

    private static string BuildNormalizedInput(
        string assetPath,
        string expectedSha256,
        bool isActive,
        bool dryRun)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("assetPath", assetPath);
            writer.WriteString("expectedSha256", expectedSha256);
            writer.WriteBoolean("isActive", isActive);
            writer.WriteBoolean("dryRun", dryRun);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<WorkspaceFileFingerprint?> ReadFingerprintAsync(
        IWorkspaceKernel workspace,
        string projectPath,
        string assetPath,
        CancellationToken cancellationToken)
    {
        var result = await workspace.FingerprintAsync(
                new WorkspaceFingerprintRequest
                {
                    ProjectPath = projectPath,
                    RelativePaths = new List<string> { assetPath }
                },
                cancellationToken)
            .ConfigureAwait(false);
        return result.Files.Count == 1 ? result.Files[0] : null;
    }

    private static async Task<OperationResult> MapResultAsync(
        OperationRequest request,
        IOperationContext context,
        EditorOperationResult result,
        string assetPath,
        string expectedSha256,
        bool requestedIsActive,
        bool effectiveDryRun,
        string preSha256,
        CancellationToken cancellationToken)
    {
        if (result.Status == OperationStatus.Cancelled)
        {
            return effectiveDryRun
                ? OperationResults.Failure<PrefabActivationData>(
                    ErrorCodes.Cancelled,
                    "editor",
                    "project.prefab-set-active dryRun 已取消；资产未被写入。",
                    OperationStatus.Cancelled)
                : Uncertain(
                    "Editor 写入在提交后取消，无法确认资产状态；禁止自动重试。",
                    WriteDetails(assetPath, "write_cancelled"));
        }

        if (result.Status == OperationStatus.Uncertain)
        {
            var first = result.Errors.Count > 0 ? result.Errors[0] : null;
            return Uncertain(
                first?.Message ?? "Editor 返回 Uncertain，无法确认资产状态。",
                first?.Details);
        }

        if (result.Status != OperationStatus.Completed)
        {
            var first = result.Errors.Count > 0 ? result.Errors[0] : null;
            return OperationResults.Failure<PrefabActivationData>(
                first?.Code ?? ErrorCodes.ExecutionFailed,
                "editor",
                first?.Message ?? "Editor 未返回完成结果。",
                details: first?.Details);
        }

        if (!TryParseCompleted(result.OutputJson, out var fields, out var parseError))
        {
            return CompletedAnomaly(effectiveDryRun, assetPath, parseError);
        }

        // Completed 结果必须与请求和被观察到的磁盘状态自洽；任何不一致
        // 在非 dry-run 下都意味着写入结果不可确认。
        if (!string.Equals(fields.AssetPath, assetPath, StringComparison.OrdinalIgnoreCase)
            || fields.DryRun != effectiveDryRun
            || !string.Equals(
                fields.PreviousSha256,
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return CompletedAnomaly(
                effectiveDryRun,
                assetPath,
                "Editor 返回的 assetPath、dryRun 或 previousSha256 与请求不一致。");
        }

        if (effectiveDryRun)
        {
            // dry-run 不写盘：IsActive 是请求的目标预览，Changed 必须等价于
            // “当前状态与目标状态不同”；Verified 在未保存时不可能为 true。
            if (fields.IsActive != requestedIsActive
                || fields.Changed != (fields.WasActive != fields.IsActive))
            {
                return CompletedAnomaly(
                    effectiveDryRun,
                    assetPath,
                    "Editor 返回的 isActive/changed 与 dryRun 预览语义不一致。");
            }

            var dryRunResult = new OperationResult<PrefabActivationData>
            {
                Status = OperationStatus.Completed,
                Data = new PrefabActivationData
                {
                    ProjectPath = request.ProjectPath,
                    AssetPath = fields.AssetPath,
                    PreviousSha256 = fields.PreviousSha256,
                    NewSha256 = fields.NewSha256,
                    WasActive = fields.WasActive,
                    IsActive = fields.IsActive,
                    Changed = fields.Changed,
                    Saved = fields.Saved,
                    DryRun = true,
                    UndoGroup = fields.UndoGroup,
                    Verified = fields.Verified
                },
                Proof = new ProofData
                {
                    InputFingerprint = preSha256,
                    OutputFingerprint = preSha256,
                    Evidence = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["assetPath"] = fields.AssetPath,
                        ["requestedIsActive"] = requestedIsActive ? "true" : "false",
                        ["wasActive"] = fields.WasActive ? "true" : "false",
                        ["isActive"] = fields.IsActive ? "true" : "false",
                        ["changed"] = fields.Changed ? "true" : "false",
                        ["dryRun"] = "true"
                    }
                }
            };
            dryRunResult.Warnings.Add(new KernelWarning
            {
                Code = "PREFAB_SET_ACTIVE_DRY_RUN",
                Message = "dryRun 只校验并预览目标 activeSelf；未创建 Undo、未保存资产。"
            });
            return dryRunResult;
        }

        // 真实写必须证明发生了持久化变化：未变化说明 Editor 未进入保存路径，
        // 无法用本次结果证明请求已被写入，交由调用方读取快照确认。
        if (!fields.Changed)
        {
            return Uncertain(
                "Editor 报告真实写未产生资产变化；真实写要求文件 SHA 发生变化，请读取资产快照确认当前状态。",
                WriteDetails(assetPath, "write_not_changed"));
        }

        if (!fields.Saved
            || !fields.Verified
            || fields.IsActive != requestedIsActive
            || fields.WasActive == fields.IsActive
            || string.Equals(
                fields.NewSha256,
                fields.PreviousSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Uncertain(
                "Editor 报告资产已变化，但保存、回读或哈希验证不完整；无法确认写入结果。",
                WriteDetails(assetPath, "write_unverified"));
        }

        // 以磁盘指纹作为最终证据：Unity 报告的新哈希必须与真实文件一致，
        // 否则说明保存/导入尚未完成或结果不可信。
        WorkspaceFileFingerprint? postFingerprint;
        try
        {
            postFingerprint = await ReadFingerprintAsync(
                    context.Workspace,
                    request.ProjectPath,
                    assetPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkspaceException exception)
        {
            return Uncertain(
                "写入后无法读取 Workspace 指纹，无法确认资产状态。",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["assetPath"] = assetPath,
                    ["reason"] = "post_fingerprint_failed",
                    ["originalCode"] = exception.Code
                });
        }

        if (postFingerprint is null
            || !postFingerprint.Exists
            || !string.Equals(
                postFingerprint.Sha256,
                fields.NewSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Uncertain(
                "写入后的磁盘 SHA-256 与 Editor 报告不一致，无法确认资产状态。",
                WriteDetails(assetPath, "post_fingerprint_mismatch"));
        }

        var completed = new OperationResult<PrefabActivationData>
        {
            Status = OperationStatus.Completed,
            Data = new PrefabActivationData
            {
                ProjectPath = request.ProjectPath,
                AssetPath = fields.AssetPath,
                PreviousSha256 = fields.PreviousSha256,
                NewSha256 = postFingerprint.Sha256,
                WasActive = fields.WasActive,
                IsActive = fields.IsActive,
                Changed = fields.Changed,
                Saved = fields.Saved,
                DryRun = false,
                UndoGroup = fields.UndoGroup,
                Verified = fields.Verified
            },
            Proof = new ProofData
            {
                InputFingerprint = preSha256,
                OutputFingerprint = postFingerprint.Sha256,
                Evidence = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["assetPath"] = fields.AssetPath,
                    ["requestedIsActive"] = requestedIsActive ? "true" : "false",
                    ["wasActive"] = fields.WasActive ? "true" : "false",
                    ["isActive"] = fields.IsActive ? "true" : "false",
                    ["changed"] = fields.Changed ? "true" : "false",
                    ["saved"] = fields.Saved ? "true" : "false",
                    ["dryRun"] = "false",
                    ["undoGroup"] = fields.UndoGroup,
                    ["verified"] = fields.Verified ? "true" : "false"
                }
            }
        };

        completed.ChangedAssets.Add(fields.AssetPath);
        return completed;
    }

    /// <summary>
    /// 解析上游 PrefabActivationData 字段。所有字段都必须存在且类型正确，
    /// 缺失不能用默认值补齐。
    /// </summary>
    private static bool TryParseCompleted(
        string? outputJson,
        out ActivationFields fields,
        out string error)
    {
        fields = new ActivationFields();
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(outputJson) ? "{}" : outputJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "结果根节点必须是 JSON 对象。";
                return false;
            }

            if (!TryReadString(root, "assetPath", out var assetPath, out error)
                || !TryReadString(root, "previousSha256", out var previousSha256, out error)
                || !TryReadString(root, "newSha256", out var newSha256, out error)
                || !TryReadBoolean(root, "wasActive", out var wasActive, out error)
                || !TryReadBoolean(root, "isActive", out var isActive, out error)
                || !TryReadBoolean(root, "changed", out var changed, out error)
                || !TryReadBoolean(root, "saved", out var saved, out error)
                || !TryReadBoolean(root, "dryRun", out var dryRun, out error)
                || !TryReadString(root, "undoGroup", out var undoGroup, out error)
                || !TryReadBoolean(root, "verified", out var verified, out error))
            {
                return false;
            }

            fields = new ActivationFields
            {
                AssetPath = assetPath,
                PreviousSha256 = previousSha256,
                NewSha256 = newSha256,
                WasActive = wasActive,
                IsActive = isActive,
                Changed = changed,
                Saved = saved,
                DryRun = dryRun,
                UndoGroup = undoGroup,
                Verified = verified
            };
            return true;
        }
        catch (JsonException)
        {
            error = "Editor 返回的 typed 结果不是合法 JSON。";
            return false;
        }
    }

    private static OperationResult CompletedAnomaly(
        bool effectiveDryRun,
        string assetPath,
        string message) =>
        effectiveDryRun
            ? OperationResults.Failure<PrefabActivationData>(
                ErrorCodes.ExecutionFailed,
                "editor",
                message)
            : Uncertain(
                message + " 写入结果无法确认。",
                WriteDetails(assetPath, "completed_result_inconsistent"));

    private static OperationResult<PrefabActivationData> Uncertain(
        string message,
        IReadOnlyDictionary<string, string>? details = null) =>
        OperationResults.Failure<PrefabActivationData>(
            ErrorCodes.ExecutionUncertain,
            "editor",
            message,
            OperationStatus.Uncertain,
            details);

    private static Dictionary<string, string> WriteDetails(string assetPath, string reason) =>
        new(StringComparer.Ordinal)
        {
            ["assetPath"] = assetPath,
            ["reason"] = reason
        };

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

    /// <summary>上游 completed 结果中进入公共 DTO 的字段集合。</summary>
    private sealed class ActivationFields
    {
        public string AssetPath { get; set; } = string.Empty;
        public string PreviousSha256 { get; set; } = string.Empty;
        public string NewSha256 { get; set; } = string.Empty;
        public bool WasActive { get; set; }
        public bool IsActive { get; set; }
        public bool Changed { get; set; }
        public bool Saved { get; set; }
        public bool DryRun { get; set; }
        public string UndoGroup { get; set; } = string.Empty;
        public bool Verified { get; set; }
    }
}
