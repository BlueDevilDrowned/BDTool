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
/// project.inspect：通过 Editor Provider 调用只读 `get_hierarchy`，把上游响应
/// 收敛为 ProjectInspectData。M2 只读取场景层级，不执行场景或资产写入。
/// 调用方输入在 Submit 之前完成严格类型校验、范围校验和白名单规范化，
/// 不把原始字符串连同未知字段直接转发给 Editor Provider。
/// </summary>
public sealed class ProjectInspectHandler : IOperationHandler
{
    public const string StableOperationId = "project.inspect";

    /// <summary>未显式传入 maxDepth 时的递归深度；0 表示只返回根节点。</summary>
    public const int DefaultMaxDepth = 8;
    public const int MaximumMaxDepth = 32;

    /// <summary>未显式传入 maxObjects 时的节点预算。</summary>
    public const int DefaultMaxObjects = 2000;
    public const int MinimumMaxObjects = 1;
    public const int MaximumMaxObjects = 10000;

    /// <summary>上游 get_hierarchy 接受的字段集合；其余字段在转发前拒绝。</summary>
    private static readonly string[] s_allowedInputProperties =
    {
        "rootPath",
        "includeComponents",
        "includeInactive",
        "includeLayers",
        "includeTags",
        "includeTransform",
        "maxDepth",
        "maxObjects",
        "nameOnly"
    };

    public string OperationId => StableOperationId;

    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        IOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // 先在本地完成校验与规范化：非法输入必须在 Submit 之前返回
        // INVALID_ARGUMENT，不能先占用 Editor Job 再失败。
        if (!TryPrepareInput(
                request.InputJson,
                out var normalizedInput,
                out var maxDepth,
                out var maxObjects,
                out var inputError))
        {
            return OperationResults.Failure<ProjectInspectData>(
                ErrorCodes.InvalidArgument,
                "input",
                inputError);
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
                    InputJson = normalizedInput,
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
                    return MapResult(request, result, maxDepth, maxObjects);
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    await TryCancelAsync(context.Editor, handle).ConfigureAwait(false);
                    return OperationResults.Failure<ProjectInspectData>(
                        ErrorCodes.Timeout,
                        "editor",
                        "project.inspect 超时。");
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

            return OperationResults.Failure<ProjectInspectData>(
                ErrorCodes.Cancelled,
                "editor",
                "project.inspect 已取消。",
                OperationStatus.Cancelled);
        }
        catch (EditorException exception)
        {
            return OperationResults.Failure<ProjectInspectData>(
                exception.Code,
                "editor",
                exception.Message,
                details: new Dictionary<string, string>(
                    exception.Details,
                    StringComparer.Ordinal));
        }
        catch (NotSupportedException)
        {
            return OperationResults.Failure<ProjectInspectData>(
                ErrorCodes.CapabilityUnsupported,
                "editor",
                "当前运行时未装配 Editor Provider。");
        }
    }

    /// <summary>
    /// 校验输入字段的类型和取值范围，并生成只含白名单字段的规范化 JSON。
    /// 布尔、字符串和整数均严格匹配 JSON 类型；缺失的可选字段使用稳定默认值，
    /// 保证同一请求在不同上游默认值下行为一致。
    /// </summary>
    private static bool TryPrepareInput(
        string? inputJson,
        out string normalizedInput,
        out int maxDepth,
        out int maxObjects,
        out string error)
    {
        normalizedInput = "{}";
        maxDepth = DefaultMaxDepth;
        maxObjects = DefaultMaxObjects;
        error = string.Empty;

        try
        {
            using var document = OperationInputParser.ParseObject(
                inputJson,
                s_allowedInputProperties);
            var root = document.RootElement;

            var rootPath = OperationInputParser.GetString(root, "rootPath") ?? string.Empty;
            var includeComponents = OperationInputParser.GetBoolean(root, "includeComponents");
            // 上游 get_hierarchy 的 includeInactive 默认为 true；这里显式写回默认值，
            // 避免调用方省略字段时随上游实现变化而改变结果。
            var includeInactive = OperationInputParser.GetBoolean(root, "includeInactive", true);
            var includeLayers = OperationInputParser.GetBoolean(root, "includeLayers");
            var includeTags = OperationInputParser.GetBoolean(root, "includeTags");
            var includeTransform = OperationInputParser.GetBoolean(root, "includeTransform");
            var nameOnly = OperationInputParser.GetBoolean(root, "nameOnly");

            maxDepth = OperationInputParser.GetInt32(root, "maxDepth", DefaultMaxDepth);
            if (maxDepth < 0 || maxDepth > MaximumMaxDepth)
            {
                throw new OperationInputException(
                    $"字段 maxDepth 必须在 0 到 {MaximumMaxDepth} 之间。");
            }

            maxObjects = OperationInputParser.GetInt32(root, "maxObjects", DefaultMaxObjects);
            if (maxObjects < MinimumMaxObjects || maxObjects > MaximumMaxObjects)
            {
                throw new OperationInputException(
                    $"字段 maxObjects 必须在 {MinimumMaxObjects} 到 {MaximumMaxObjects} 之间。");
            }

            normalizedInput = BuildNormalizedInput(
                rootPath,
                includeComponents,
                includeInactive,
                includeLayers,
                includeTags,
                includeTransform,
                maxDepth,
                maxObjects,
                nameOnly);
            return true;
        }
        catch (OperationInputException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>按固定字段顺序写入规范化 JSON，所有值均已通过类型和范围校验。</summary>
    private static string BuildNormalizedInput(
        string rootPath,
        bool includeComponents,
        bool includeInactive,
        bool includeLayers,
        bool includeTags,
        bool includeTransform,
        int maxDepth,
        int maxObjects,
        bool nameOnly)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("rootPath", rootPath);
            writer.WriteBoolean("includeComponents", includeComponents);
            writer.WriteBoolean("includeInactive", includeInactive);
            writer.WriteBoolean("includeLayers", includeLayers);
            writer.WriteBoolean("includeTags", includeTags);
            writer.WriteBoolean("includeTransform", includeTransform);
            writer.WriteNumber("maxDepth", maxDepth);
            writer.WriteNumber("maxObjects", maxObjects);
            writer.WriteBoolean("nameOnly", nameOnly);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static OperationResult MapResult(
        OperationRequest request,
        EditorOperationResult result,
        int maxDepth,
        int maxObjects)
    {
        if (result.Status == OperationStatus.Cancelled)
        {
            return OperationResults.Failure<ProjectInspectData>(
                ErrorCodes.Cancelled,
                "editor",
                "project.inspect 已取消。",
                OperationStatus.Cancelled);
        }

        if (result.Status != OperationStatus.Completed)
        {
            var first = result.Errors.Count > 0 ? result.Errors[0] : null;
            return OperationResults.Failure<ProjectInspectData>(
                first?.Code ?? ErrorCodes.ExecutionFailed,
                "editor",
                first?.Message ?? "Editor 未返回完成结果。",
                details: first?.Details);
        }

        try
        {
            // 每个层级节点会贡献“对象 + children 数组”两层 JSON 深度，因此解析
            // 上限按 2 * maxDepth 预留；超深响应在解析阶段就被拒绝，避免异常响应
            // 导致递归映射超出预算。
            var documentOptions = new JsonDocumentOptions
            {
                MaxDepth = Math.Clamp((maxDepth * 2) + 16, 32, 128)
            };
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(result.OutputJson) ? "{}" : result.OutputJson,
                documentOptions);
            var root = document.RootElement;
            var data = new ProjectInspectData
            {
                ProjectPath = request.ProjectPath,
                UnityVersion = ReadString(root, "unityVersion"),
                SceneName = ReadString(root, "sceneName"),
                IsPrefabMode = ReadBoolean(root, "isPrefabMode")
            };

            var mappedCount = 0;
            var truncated = false;
            if (root.TryGetProperty("hierarchy", out var hierarchy)
                && hierarchy.ValueKind == JsonValueKind.Array)
            {
                AppendNodes(
                    hierarchy,
                    data.Hierarchy,
                    depth: 0,
                    maxDepth,
                    maxObjects,
                    ref mappedCount,
                    ref truncated);
            }

            if (truncated)
            {
                // 不把被截断的部分层级伪装成完整结果；Provider 正常执行时
                // 会遵守 maxDepth/maxObjects，触发此分支说明上游响应异常。
                return OperationResults.Failure<ProjectInspectData>(
                    ErrorCodes.ExecutionFailed,
                    "editor",
                    "Editor 返回的层级超过请求的 maxDepth/maxObjects 上限。");
            }

            // 上游 objectCount 是顶层根节点数（unity-cli 中为 hierarchy.Count），
            // 不是递归节点总数，因此不采用为 DTO 的 objectCount；
            // typed 结果始终表示本次映射得到的递归节点总数。
            data.ObjectCount = mappedCount;

            return new OperationResult<ProjectInspectData>
            {
                Status = OperationStatus.Completed,
                Data = data
            };
        }
        catch (JsonException)
        {
            return OperationResults.Failure<ProjectInspectData>(
                ErrorCodes.ExecutionFailed,
                "editor",
                "Editor 返回的 typed 结果格式无效。");
        }
    }

    /// <summary>递归映射一组兄弟节点，并在节点预算耗尽时标记截断。</summary>
    private static void AppendNodes(
        JsonElement array,
        List<ProjectInspectNode> target,
        int depth,
        int maxDepth,
        int nodeBudget,
        ref int mappedCount,
        ref bool truncated)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (mappedCount >= nodeBudget)
            {
                truncated = true;
                return;
            }

            target.Add(MapNode(
                item,
                depth,
                maxDepth,
                nodeBudget,
                ref mappedCount,
                ref truncated));
            if (truncated)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 映射单个节点并递归处理 `children`。depth 从 0 开始，与上游语义一致：
    /// 仅当 depth &lt; maxDepth 时继续向下，depth == maxDepth 的节点保留但不展开。
    /// 上游若返回超过请求深度的子节点，视为响应越界并标记截断。
    /// </summary>
    private static ProjectInspectNode MapNode(
        JsonElement item,
        int depth,
        int maxDepth,
        int nodeBudget,
        ref int mappedCount,
        ref bool truncated)
    {
        mappedCount++;
        var node = new ProjectInspectNode
        {
            Name = ReadString(item, "name"),
            Path = ReadString(item, "path"),
            Active = ReadBoolean(item, "isActive")
        };

        if (item.TryGetProperty("components", out var components)
            && components.ValueKind == JsonValueKind.Array)
        {
            foreach (var component in components.EnumerateArray())
            {
                if (component.ValueKind == JsonValueKind.String)
                {
                    node.Components.Add(component.GetString() ?? string.Empty);
                }
            }
        }

        if (!item.TryGetProperty("children", out var children)
            || children.ValueKind != JsonValueKind.Array)
        {
            return node;
        }

        if (depth >= maxDepth)
        {
            if (children.GetArrayLength() > 0)
            {
                truncated = true;
            }

            return node;
        }

        AppendNodes(
            children,
            node.Children,
            depth + 1,
            maxDepth,
            nodeBudget,
            ref mappedCount,
            ref truncated);
        return node;
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
            // 取消是尽力而为；原 Job 已不可达时不能让清理异常覆盖原结果。
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

}
