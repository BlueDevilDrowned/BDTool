using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BDTool.Contracts;
using BDTool.Contracts.Capabilities;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;
using BDTool.Provider.Official;
using BDTool.Tests.Support;

namespace BDTool.Tests.Editor;

/// <summary>
/// Official Provider 的 Contract 测试。通过注入官方协议端口验证状态、层级、
/// Prefab 读写、能力差异和写失败不确定性，不依赖正在运行的 Unity Editor。
/// </summary>
public sealed class OfficialEditorKernelTests : IDisposable
{
    private readonly TemporaryProjectDirectory _project = new();

    [Fact]
    public async Task Describe_ExposesOfficialIdentityAndCapabilities()
    {
        var client = new FakePipelineClient();
        using var kernel = CreateKernel(client);

        var descriptor = await kernel.DescribeAsync(CancellationToken.None);

        Assert.Equal("official", descriptor.Id);
        Assert.Equal(ProviderKinds.Editor, descriptor.Kind);
        Assert.Equal("0.10.0-exp.1", descriptor.ImplementationVersion);
        Assert.True(descriptor.Capabilities[CapabilityIds.EditorStatus]);
        Assert.True(descriptor.Capabilities[CapabilityIds.EditorToolRead]);
        Assert.True(descriptor.Capabilities[CapabilityIds.EditorToolWrite]);
    }

    [Fact]
    public async Task GetStatus_MapsOfficialReadyState()
    {
        var client = new FakePipelineClient();
        using var kernel = CreateKernel(client);

        var status = await kernel.GetStatusAsync(
            new EditorStatusRequest { ProjectPath = _project.ProjectPath },
            CancellationToken.None);

        Assert.Equal("ready", status.State);
        Assert.Equal(Path.GetFullPath(_project.ProjectPath), status.ProjectPath);
        Assert.Equal("6000.6.0f1", status.UnityVersion);
        Assert.Equal(4242, status.ProcessId);
    }

    [Fact]
    public async Task Inspect_NormalizesOfficialHierarchyForExistingHandler()
    {
        var client = new FakePipelineClient();
        client.Commands["scene get_hierarchy"] = Json(
            """
            {
              "sceneName": "Main",
              "scenePath": "Assets/Main.unity",
              "roots": [{
                "name": "Root",
                "hierarchyPath": "/Root",
                "activeSelf": true,
                "components": ["Transform"],
                "children": [{
                  "name": "Child",
                  "hierarchyPath": "/Root/Child",
                  "activeSelf": false,
                  "components": ["Transform"],
                  "children": []
                }]
              }]
            }
            """);
        using var kernel = CreateKernel(client);
        var handle = await kernel.SubmitAsync(
            Request(
                "project.inspect",
                """{"rootPath":"/Root","includeComponents":true,"includeInactive":true,"maxDepth":2,"maxObjects":10}"""),
            CancellationToken.None);

        var result = await WaitForResultAsync(kernel, handle);

        Assert.Equal(OperationStatus.Completed, result.Status);
        using var output = JsonDocument.Parse(result.OutputJson);
        Assert.Equal("6000.6.0f1", output.RootElement.GetProperty("unityVersion").GetString());
        var root = Assert.Single(output.RootElement.GetProperty("hierarchy").EnumerateArray());
        Assert.Equal("/Root", root.GetProperty("path").GetString());
        Assert.Equal("Transform", root.GetProperty("components")[0].GetString());
        Assert.Equal("Child", root.GetProperty("children")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task AssetSnapshot_UsesFixedScriptEvalAndReturnsTypedJson()
    {
        var client = new FakePipelineClient();
        client.Commands["script eval"] = Json(
            """
            {
              "ok": true,
              "assetPath": "Assets/Probe.prefab",
              "exists": true,
              "sha256": "abc",
              "rootObjectName": "Probe",
              "childCount": 2,
              "unityVersion": "6000.6.0f1"
            }
            """);
        using var kernel = CreateKernel(client);
        var handle = await kernel.SubmitAsync(
            Request("project.asset-snapshot", """{"assetPath":"Assets/Probe.prefab"}"""),
            CancellationToken.None);

        var result = await WaitForResultAsync(kernel, handle);

        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.Contains("Assets/Probe.prefab", result.OutputJson, StringComparison.Ordinal);
        var call = Assert.Single(client.Calls);
        Assert.Equal("script eval", call.Command);
        Assert.Contains("AssetDatabase.LoadAssetAtPath<GameObject>", Assert.IsType<string>(call.Parameters["code"]));
    }

    [Fact]
    public async Task PrefabSetActive_UsesScriptEvalWithValidatedInputs()
    {
        var client = new FakePipelineClient();
        client.Commands["script eval"] = Json(
            """
            {
              "ok": true,
              "assetPath": "Assets/Probe.prefab",
              "previousSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "newSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "wasActive": true,
              "isActive": false,
              "changed": true,
              "saved": true,
              "dryRun": false,
              "undoGroup": "",
              "verified": true
            }
            """);
        using var kernel = CreateKernel(client);
        var handle = await kernel.SubmitAsync(
            new EditorOperationRequest
            {
                OperationId = "project.prefab-set-active",
                ProjectPath = _project.ProjectPath,
                InputJson =
                    """{"assetPath":"Assets/Probe.prefab","expectedSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","isActive":false,"dryRun":false}""",
                Mutating = true,
                WriteScope = new List<string> { "Assets/Probe.prefab" }
            },
            CancellationToken.None);

        var result = await WaitForResultAsync(kernel, handle);

        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.True(JsonDocument.Parse(result.OutputJson).RootElement.GetProperty("verified").GetBoolean());
        var code = Assert.IsType<string>(Assert.Single(client.Calls).Parameters["code"]);
        Assert.Contains("root.SetActive(targetActive)", code, StringComparison.Ordinal);
        Assert.Contains("SaveAsPrefabAsset", code, StringComparison.Ordinal);
        Assert.Contains("isActive = finalActive", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AkioSpecificProbe_ReturnsCapabilityUnsupported()
    {
        var client = new FakePipelineClient();
        using var kernel = CreateKernel(client);

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.SubmitAsync(Request("project.probe", "{}"), CancellationToken.None));

        Assert.Equal(ErrorCodes.CapabilityUnsupported, exception.Code);
        Assert.Contains("project.probe", exception.Details["operationId"], StringComparison.Ordinal);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task WriteInput_RejectsNonHexExpectedShaBeforeHttp()
    {
        var client = new FakePipelineClient();
        using var kernel = CreateKernel(client);

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.SubmitAsync(
                new EditorOperationRequest
                {
                    OperationId = "project.prefab-set-active",
                    ProjectPath = _project.ProjectPath,
                    InputJson =
                        """{"assetPath":"Assets/Probe.prefab","expectedSha256":"not-a-hash","isActive":false,"dryRun":true}""",
                    Mutating = true,
                    DryRun = true,
                    WriteScope = new List<string> { "Assets/Probe.prefab" }
                },
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task WriteTransportFailure_DoesNotFallbackAndReturnsUncertain()
    {
        var client = new FakePipelineClient();
        client.ExecuteFailure = new OfficialPipelineException(
            ErrorCodes.ExecutionFailed,
            "transport lost");
        using var kernel = CreateKernel(client);
        var handle = await kernel.SubmitAsync(
            new EditorOperationRequest
            {
                OperationId = "project.prefab-set-active",
                ProjectPath = _project.ProjectPath,
                InputJson =
                    """{"assetPath":"Assets/Probe.prefab","expectedSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","isActive":false,"dryRun":false}""",
                Mutating = true,
                WriteScope = new List<string> { "Assets/Probe.prefab" }
            },
            CancellationToken.None);

        var result = await WaitForResultAsync(kernel, handle);

        Assert.Equal(OperationStatus.Uncertain, result.Status);
        Assert.Equal(ErrorCodes.ExecutionUncertain, Assert.Single(result.Errors).Code);
        Assert.Single(client.Calls);
    }

    [Fact]
    public async Task MissingEditorStatus_MapsToNotFound()
    {
        var client = new FakePipelineClient
        {
            StatusFailure = new OfficialPipelineException(ErrorCodes.EditorNotFound, "no editor")
        };
        using var kernel = CreateKernel(client);

        var status = await kernel.GetStatusAsync(
            new EditorStatusRequest { ProjectPath = _project.ProjectPath },
            CancellationToken.None);

        Assert.Equal("not-found", status.State);
    }

    [Fact]
    public async Task CancelAsync_StopsRunningJobAndReturnsCancelled()
    {
        var client = new FakePipelineClient { DelayUntilCancelled = true };
        using var kernel = CreateKernel(client);
        var handle = await kernel.SubmitAsync(
            Request("project.asset-snapshot", """{"assetPath":"Assets/Probe.prefab"}"""),
            CancellationToken.None);

        await kernel.CancelAsync(handle, CancellationToken.None);
        var result = await WaitForResultAsync(kernel, handle);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(ErrorCodes.Cancelled, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task CommandTimeout_ReturnsStableTimeout()
    {
        var client = new FakePipelineClient { DelayUntilCancelled = true };
        using var kernel = new OfficialEditorKernel(
            new OfficialEditorOptions
            {
                ProjectPath = _project.ProjectPath,
                DefaultTimeoutMilliseconds = 150,
                CompletedJobRetentionMilliseconds = 5000,
                CancelWaitMilliseconds = 500
            },
            client);
        var handle = await kernel.SubmitAsync(
            Request("project.asset-snapshot", """{"assetPath":"Assets/Probe.prefab"}"""),
            CancellationToken.None);

        var result = await WaitForResultAsync(kernel, handle);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.Timeout, Assert.Single(result.Errors).Code);
    }

    private OfficialEditorKernel CreateKernel(FakePipelineClient client) =>
        new(
            new OfficialEditorOptions
            {
                ProjectPath = _project.ProjectPath,
                DefaultTimeoutMilliseconds = 2000,
                CompletedJobRetentionMilliseconds = 5000,
                CancelWaitMilliseconds = 500
            },
            client);

    private EditorOperationRequest Request(string operationId, string inputJson) => new()
    {
        OperationId = operationId,
        ProjectPath = _project.ProjectPath,
        InputJson = inputJson
    };

    private static async Task<EditorOperationResult> WaitForResultAsync(
        IEditorKernel kernel,
        OperationHandle handle)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var result = await kernel.GetResultAsync(handle, CancellationToken.None);
            if (result.Status is not OperationStatus.Queued and not OperationStatus.Running)
            {
                return result;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Official Editor Job 未在测试期限完成。");
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public void Dispose() => _project.Dispose();

    private sealed class FakePipelineClient : IOfficialPipelineClient
    {
        public Dictionary<string, JsonElement> Commands { get; } =
            new(StringComparer.Ordinal);

        public List<Call> Calls { get; } = new();
        public OfficialPipelineException? StatusFailure { get; set; }
        public OfficialPipelineException? ExecuteFailure { get; set; }
        public bool DelayUntilCancelled { get; set; }

        public Task<OfficialPipelineStatusSnapshot> GetStatusAsync(
            string projectPath,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StatusFailure is not null)
            {
                throw StatusFailure;
            }

            return Task.FromResult(new OfficialPipelineStatusSnapshot
            {
                State = "ready",
                ProjectPath = projectPath,
                UnityVersion = "6000.6.0f1",
                ProcessId = 4242
            });
        }

        public async Task<JsonElement> ExecuteAsync(
            string projectPath,
            string command,
            IReadOnlyDictionary<string, object?> parameters,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new Call(command, parameters));
            if (ExecuteFailure is not null)
            {
                throw ExecuteFailure;
            }

            if (DelayUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (!Commands.TryGetValue(command, out var result))
            {
                throw new InvalidOperationException("缺少命令结果：" + command);
            }

            return result;
        }

        public void Dispose()
        {
        }
    }

    private sealed record Call(
        string Command,
        IReadOnlyDictionary<string, object?> Parameters);
}
