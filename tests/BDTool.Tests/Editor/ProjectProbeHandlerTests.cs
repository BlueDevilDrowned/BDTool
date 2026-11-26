using System.Diagnostics;
using BDTool.Contracts;
using BDTool.Contracts.Artifacts;
using BDTool.Contracts.Capabilities;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Projects;
using BDTool.Contracts.Providers;
using BDTool.Contracts.Workspace;
using BDTool.Operations.Projects;

namespace BDTool.Tests.Editor;

public sealed class ProjectProbeHandlerTests
{
    [Fact]
    public async Task Execute_MapsProbeOutputToTypedData()
    {
        var handler = new ProjectProbeHandler();
        var editor = new StubEditor
        {
            TerminalResult = Completed(
                """
                {
                  "product": "BDTool",
                  "unityVersion": "6000.0.32f1",
                  "platform": "WindowsEditor",
                  "sceneName": "Bootstrap",
                  "scenePath": "Assets/Scenes/Bootstrap.unity",
                  "sceneIsDirty": true,
                  "rootObjectCount": 12,
                  "observedAtUtc": "2026-11-26T08:00:00Z"
                }
                """)
        };

        var result = await ExecuteAsync(handler, editor, "{}");

        var typed = Assert.IsType<OperationResult<ProjectProbeData>>(result);
        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.Equal("BDTool", typed.Data.Product);
        Assert.Equal("6000.0.32f1", typed.Data.UnityVersion);
        Assert.Equal("WindowsEditor", typed.Data.Platform);
        Assert.Equal("Bootstrap", typed.Data.SceneName);
        Assert.Equal("Assets/Scenes/Bootstrap.unity", typed.Data.ScenePath);
        Assert.True(typed.Data.SceneIsDirty);
        Assert.Equal(12, typed.Data.RootObjectCount);
        Assert.Equal("2026-11-26T08:00:00Z", typed.Data.ObservedAtUtc);

        Assert.Equal(1, editor.SubmitCalls);
        Assert.Equal("project.probe", editor.LastRequest!.OperationId);
        Assert.Equal("{}", editor.LastRequest.InputJson);
    }

    [Theory]
    [InlineData("""{"maxDepth":8}""")]
    [InlineData("""{"product":"x"}""")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    public async Task Execute_RejectsNonEmptyInputBeforeSubmit(string inputJson)
    {
        var handler = new ProjectProbeHandler();
        var editor = new StubEditor();

        var result = await ExecuteAsync(handler, editor, inputJson);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Single(result.Errors).Code);
        Assert.Equal(0, editor.SubmitCalls);
        Assert.Null(editor.LastRequest);
    }

    [Fact]
    public async Task Execute_MapsProviderFailureToStableEnvelope()
    {
        var handler = new ProjectProbeHandler();
        var editor = new StubEditor
        {
            TerminalResult = new EditorOperationResult
            {
                Status = OperationStatus.Failed,
                Errors =
                {
                    new KernelError
                    {
                        Code = ErrorCodes.EditorDisconnected,
                        Message = "Bridge disconnected",
                        Stage = "editor"
                    }
                }
            }
        };

        var result = await ExecuteAsync(handler, editor, "{}");

        var typed = Assert.IsType<OperationResult<ProjectProbeData>>(result);
        Assert.Equal(OperationStatus.Failed, typed.Status);
        Assert.Equal(ErrorCodes.EditorDisconnected, Assert.Single(typed.Errors).Code);
    }

    [Fact]
    public async Task Execute_RejectsCompletedOutputWithMissingFields()
    {
        var handler = new ProjectProbeHandler();
        var editor = new StubEditor
        {
            // Completed 但缺少 typed 字段：不得用默认值伪造成成功。
            TerminalResult = Completed("{}")
        };

        var result = await ExecuteAsync(handler, editor, "{}");

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.ExecutionFailed, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Execute_TimesOutAndCancelsProviderJob()
    {
        var handler = new ProjectProbeHandler();
        var editor = new StubEditor { KeepRunning = true };
        var stopwatch = Stopwatch.StartNew();

        var result = await handler.ExecuteAsync(
            new OperationRequest
            {
                OperationId = "project.probe",
                ProjectPath = @"C:\Project",
                InputJson = "{}",
                TimeoutSeconds = 1
            },
            new TestContext(editor),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.Timeout, Assert.Single(result.Errors).Code);
        Assert.Equal(1, editor.SubmitCalls);
        Assert.Equal(1, editor.CancelCalls);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Execute_CancellationMapsToCancelledAndCancelsProviderJob()
    {
        var handler = new ProjectProbeHandler();
        var editor = new StubEditor { KeepRunning = true };
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));

        var result = await handler.ExecuteAsync(
            new OperationRequest
            {
                OperationId = "project.probe",
                ProjectPath = @"C:\Project",
                InputJson = "{}",
                TimeoutSeconds = 30
            },
            new TestContext(editor),
            cancellation.Token);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(ErrorCodes.Cancelled, Assert.Single(result.Errors).Code);
        Assert.Equal(1, editor.SubmitCalls);
        Assert.Equal(1, editor.CancelCalls);
    }

    private static Task<OperationResult> ExecuteAsync(
        ProjectProbeHandler handler,
        StubEditor editor,
        string inputJson) =>
        handler.ExecuteAsync(
            new OperationRequest
            {
                OperationId = "project.probe",
                ProjectPath = @"C:\Project",
                InputJson = inputJson
            },
            new TestContext(editor),
            CancellationToken.None);

    private static EditorOperationResult Completed(string outputJson) =>
        new()
        {
            Status = OperationStatus.Completed,
            OutputJson = outputJson
        };

    private sealed class TestContext : IOperationContext
    {
        public TestContext(IEditorKernel editor)
        {
            Editor = editor;
        }

        public IWorkspaceKernel Workspace => throw new NotSupportedException();
        public IEditorKernel Editor { get; }
        public IArtifactStore Artifacts => throw new NotSupportedException();
        public ICapabilityInspector Capabilities => throw new NotSupportedException();
    }

    private sealed class StubEditor : IEditorKernel
    {
        public int SubmitCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public EditorOperationRequest? LastRequest { get; private set; }
        public bool KeepRunning { get; set; }

        public EditorOperationResult TerminalResult { get; set; } = new()
        {
            Status = OperationStatus.Completed,
            OutputJson = "{}"
        };

        public Task<ProviderDescriptor> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderDescriptor
            {
                Id = "akio",
                Kind = ProviderKinds.Editor,
                ContractVersion = BDToolContract.Version,
                ImplementationVersion = "0.19.1"
            });

        public Task<EditorStatus> GetStatusAsync(
            EditorStatusRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EditorStatus { State = "ready", ProjectPath = request.ProjectPath });

        public Task<OperationHandle> SubmitAsync(
            EditorOperationRequest request,
            CancellationToken cancellationToken)
        {
            SubmitCalls++;
            LastRequest = request;
            return Task.FromResult(new OperationHandle
            {
                JobId = "job-1",
                OperationId = request.OperationId,
                Status = OperationStatus.Running
            });
        }

        public Task<EditorOperationResult> GetResultAsync(
            OperationHandle handle,
            CancellationToken cancellationToken)
        {
            if (KeepRunning)
            {
                return Task.FromResult(new EditorOperationResult
                {
                    Handle = handle,
                    Status = OperationStatus.Running
                });
            }

            TerminalResult.Handle = handle;
            return Task.FromResult(TerminalResult);
        }

        public Task CancelAsync(OperationHandle handle, CancellationToken cancellationToken)
        {
            CancelCalls++;
            return Task.CompletedTask;
        }
    }
}
