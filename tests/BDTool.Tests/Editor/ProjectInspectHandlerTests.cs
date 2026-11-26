using System.Text.Json;
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

public sealed class ProjectInspectHandlerTests
{
    [Fact]
    public async Task Execute_ConvertsProviderHierarchyToTypedData()
    {
        var handler = new ProjectInspectHandler();
        var editor = new StubEditor
        {
            TerminalResult = Completed(
                """
                {
                  "hierarchy": [
                    {
                      "name": "Main Camera",
                      "path": "/Main Camera",
                      "isActive": true,
                      "components": ["Transform", "Camera"]
                    }
                  ],
                  "isPrefabMode": false,
                  "objectCount": 1,
                  "sceneName": "Bootstrap"
                }
                """)
        };

        var result = await ExecuteAsync(handler, editor, """{"includeComponents":true}""");

        var typed = Assert.IsType<OperationResult<ProjectInspectData>>(result);
        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.Equal("Bootstrap", typed.Data.SceneName);
        Assert.Equal(1, typed.Data.ObjectCount);
        var node = Assert.Single(typed.Data.Hierarchy);
        Assert.Equal("/Main Camera", node.Path);
        Assert.Equal(new[] { "Transform", "Camera" }, node.Components);
        Assert.Empty(node.Children);
    }

    [Fact]
    public async Task Execute_MapsNestedChildrenRecursively()
    {
        var handler = new ProjectInspectHandler();
        var editor = new StubEditor
        {
            TerminalResult = Completed(
                """
                {
                  "sceneName": "Bootstrap",
                  "isPrefabMode": false,
                  "objectCount": 1,
                  "hierarchy": [
                    {
                      "name": "Root",
                      "path": "/Root",
                      "isActive": true,
                      "components": ["Transform"],
                      "children": [
                        {
                          "name": "Child",
                          "path": "/Root/Child",
                          "isActive": true,
                          "children": [
                            {
                              "name": "Grandchild",
                              "path": "/Root/Child/Grandchild",
                              "isActive": false,
                              "components": ["MeshRenderer"]
                            }
                          ]
                        }
                      ]
                    }
                  ]
                }
                """)
        };

        var result = await ExecuteAsync(handler, editor, "{}");

        var typed = Assert.IsType<OperationResult<ProjectInspectData>>(result);
        Assert.Equal(OperationStatus.Completed, typed.Status);

        var root = Assert.Single(typed.Data.Hierarchy);
        Assert.Equal("/Root", root.Path);
        var child = Assert.Single(root.Children);
        Assert.Equal("/Root/Child", child.Path);
        var grandchild = Assert.Single(child.Children);
        Assert.Equal("/Root/Child/Grandchild", grandchild.Path);
        Assert.False(grandchild.Active);
        Assert.Equal(new[] { "MeshRenderer" }, grandchild.Components);
        Assert.Empty(grandchild.Children);

        // 上游 objectCount=1 只是顶层 root 数，DTO 必须表示递归节点数（Root/Child/Grandchild）。
        Assert.Equal(3, typed.Data.ObjectCount);
    }

    [Fact]
    public async Task Execute_IgnoresUpstreamObjectCountAndUsesMappedNodeCount()
    {
        var handler = new ProjectInspectHandler();
        var editor = new StubEditor
        {
            TerminalResult = Completed(
                """
                {
                  "objectCount": 7,
                  "hierarchy": [
                    { "name": "A", "path": "/A", "isActive": true },
                    { "name": "B", "path": "/B", "isActive": true, "children": [
                      { "name": "B1", "path": "/B/B1", "isActive": true }
                    ] }
                  ]
                }
                """)
        };

        var result = await ExecuteAsync(handler, editor, "{}");

        var typed = Assert.IsType<OperationResult<ProjectInspectData>>(result);
        Assert.Equal(OperationStatus.Completed, typed.Status);
        // 上游报 7（顶层 root 数），实际映射 A/B/B1 三个节点，DTO 应为 3。
        Assert.Equal(3, typed.Data.ObjectCount);
    }

    [Fact]
    public async Task Execute_NormalizesWhitelistInputBeforeSubmit()
    {
        var handler = new ProjectInspectHandler();
        var editor = new StubEditor();

        var result = await ExecuteAsync(handler, editor, """{"includeComponents":true}""");

        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.Equal(1, editor.SubmitCalls);
        var submitted = Assert.IsType<EditorOperationRequest>(editor.LastRequest);
        using var document = JsonDocument.Parse(submitted.InputJson);
        var root = document.RootElement;

        // 只保留白名单字段，且默认值被显式写回。
        Assert.Equal(9, root.EnumerateObject().Count());
        Assert.Equal(string.Empty, root.GetProperty("rootPath").GetString());
        Assert.True(root.GetProperty("includeComponents").GetBoolean());
        Assert.True(root.GetProperty("includeInactive").GetBoolean());
        Assert.False(root.GetProperty("includeLayers").GetBoolean());
        Assert.False(root.GetProperty("includeTags").GetBoolean());
        Assert.False(root.GetProperty("includeTransform").GetBoolean());
        Assert.Equal(ProjectInspectHandler.DefaultMaxDepth, root.GetProperty("maxDepth").GetInt32());
        Assert.Equal(ProjectInspectHandler.DefaultMaxObjects, root.GetProperty("maxObjects").GetInt32());
        Assert.False(root.GetProperty("nameOnly").GetBoolean());
    }

    [Fact]
    public async Task Execute_RejectsExtraInputBeforeEditorCall()
    {
        var handler = new ProjectInspectHandler();
        var editor = new StubEditor();

        var result = await ExecuteAsync(handler, editor, """{"unknown":true}""");

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Single(result.Errors).Code);
        Assert.Equal(0, editor.SubmitCalls);
    }

    [Theory]
    [InlineData("""{"maxDepth":"8"}""")]
    [InlineData("""{"maxDepth":true}""")]
    [InlineData("""{"maxDepth":null}""")]
    [InlineData("""{"maxObjects":1.5}""")]
    [InlineData("""{"maxObjects":"2000"}""")]
    [InlineData("""{"includeInactive":"true"}""")]
    [InlineData("""{"nameOnly":0}""")]
    [InlineData("""{"rootPath":42}""")]
    public async Task Execute_RejectsWrongInputTypesBeforeSubmit(string inputJson)
    {
        var handler = new ProjectInspectHandler();
        var editor = new StubEditor();

        var result = await ExecuteAsync(handler, editor, inputJson);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Single(result.Errors).Code);
        Assert.Equal(0, editor.SubmitCalls);
        Assert.Null(editor.LastRequest);
    }

    [Theory]
    [InlineData("""{"maxDepth":-1}""")]
    [InlineData("""{"maxDepth":33}""")]
    [InlineData("""{"maxObjects":0}""")]
    [InlineData("""{"maxObjects":10001}""")]
    public async Task Execute_RejectsOutOfRangeLimitsBeforeSubmit(string inputJson)
    {
        var handler = new ProjectInspectHandler();
        var editor = new StubEditor();

        var result = await ExecuteAsync(handler, editor, inputJson);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Single(result.Errors).Code);
        Assert.Equal(0, editor.SubmitCalls);
        Assert.Null(editor.LastRequest);
    }

    [Theory]
    [InlineData("""{"maxDepth":0,"maxObjects":1}""", 0, 1)]
    [InlineData("""{"maxDepth":32,"maxObjects":10000}""", 32, 10000)]
    public async Task Execute_AcceptsBoundaryLimits(
        string inputJson,
        int expectedMaxDepth,
        int expectedMaxObjects)
    {
        var handler = new ProjectInspectHandler();
        var editor = new StubEditor();

        var result = await ExecuteAsync(handler, editor, inputJson);

        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.Equal(1, editor.SubmitCalls);
        using var document = JsonDocument.Parse(editor.LastRequest!.InputJson);
        Assert.Equal(expectedMaxDepth, document.RootElement.GetProperty("maxDepth").GetInt32());
        Assert.Equal(expectedMaxObjects, document.RootElement.GetProperty("maxObjects").GetInt32());
    }

    [Fact]
    public async Task Execute_MapsHierarchyAtMaximumDepth()
    {
        var handler = new ProjectInspectHandler();
        const int maxDepth = ProjectInspectHandler.MaximumMaxDepth;
        var editor = new StubEditor
        {
            TerminalResult = Completed(BuildNestedHierarchy(maxDepth))
        };

        var result = await ExecuteAsync(
            handler,
            editor,
            $$"""{"maxDepth":{{maxDepth}}}""");

        var typed = Assert.IsType<OperationResult<ProjectInspectData>>(result);
        Assert.Equal(OperationStatus.Completed, typed.Status);
        Assert.Equal(maxDepth + 1, typed.Data.ObjectCount);

        var node = Assert.Single(typed.Data.Hierarchy);
        var measuredDepth = 0;
        while (node.Children.Count > 0)
        {
            node = Assert.Single(node.Children);
            measuredDepth++;
        }

        Assert.Equal(maxDepth, measuredDepth);
        Assert.Equal($"N{maxDepth}", node.Name);
    }

    [Fact]
    public async Task Execute_RejectsProviderHierarchyDeeperThanMaxDepth()
    {
        var handler = new ProjectInspectHandler();
        var editor = new StubEditor
        {
            // 请求 maxDepth=1，但上游返回了三层；不能静默丢弃更深层级后当作完整结果。
            TerminalResult = Completed(
                """
                {
                  "hierarchy": [
                    { "name": "Root", "path": "/Root", "isActive": true, "children": [
                      { "name": "Child", "path": "/Root/Child", "isActive": true, "children": [
                        { "name": "Leaf", "path": "/Root/Child/Leaf", "isActive": true }
                      ] }
                    ] }
                  ]
                }
                """)
        };

        var result = await ExecuteAsync(handler, editor, """{"maxDepth":1}""");

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.ExecutionFailed, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Execute_RejectsProviderHierarchyBeyondMaxObjects()
    {
        var handler = new ProjectInspectHandler();
        var editor = new StubEditor
        {
            // 请求 maxObjects=2，但上游返回三个根节点。
            TerminalResult = Completed(
                """
                {
                  "objectCount": 3,
                  "hierarchy": [
                    { "name": "A", "path": "/A", "isActive": true },
                    { "name": "B", "path": "/B", "isActive": true },
                    { "name": "C", "path": "/C", "isActive": true }
                  ]
                }
                """)
        };

        var result = await ExecuteAsync(handler, editor, """{"maxObjects":2}""");

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.ExecutionFailed, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Execute_MapsProviderFailureToStableEnvelope()
    {
        var handler = new ProjectInspectHandler();
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

        var typed = Assert.IsType<OperationResult<ProjectInspectData>>(result);
        Assert.Equal(OperationStatus.Failed, typed.Status);
        Assert.Equal(ErrorCodes.EditorDisconnected, Assert.Single(typed.Errors).Code);
    }

    private static Task<OperationResult> ExecuteAsync(
        ProjectInspectHandler handler,
        StubEditor editor,
        string inputJson) =>
        handler.ExecuteAsync(
            new OperationRequest
            {
                OperationId = "project.inspect",
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

    /// <summary>构造一条从 N0 到 N{maxDepth} 的单链层级，用于真实深度边界验证。</summary>
    private static string BuildNestedHierarchy(int maxDepth)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("{\"objectCount\":0,\"hierarchy\":[");
        for (var depth = 0; depth <= maxDepth; depth++)
        {
            if (depth > 0)
            {
                builder.Append(",\"children\":[");
            }

            builder.Append("{\"name\":\"N").Append(depth)
                .Append("\",\"path\":\"/N").Append(depth)
                .Append("\",\"isActive\":true");
        }

        for (var depth = maxDepth; depth >= 1; depth--)
        {
            builder.Append("}]");
        }

        builder.Append("}]}");
        return builder.ToString();
    }

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
        public EditorOperationRequest? LastRequest { get; private set; }

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
            TerminalResult.Handle = handle;
            return Task.FromResult(TerminalResult);
        }

        public Task CancelAsync(OperationHandle handle, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
