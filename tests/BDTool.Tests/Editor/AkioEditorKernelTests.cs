using BDTool.Contracts;
using BDTool.Contracts.Editor;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;
using BDTool.Contracts.Providers;
using BDTool.Provider.Akio;

namespace BDTool.Tests.Editor;

public sealed class AkioEditorKernelTests
{
    private static readonly string TestProjectPath = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "BDTool.Tests", nameof(AkioEditorKernelTests)));
    private static readonly string ExtendedTestProjectPath = OperatingSystem.IsWindows()
        ? @"\\?\" + TestProjectPath
        : TestProjectPath;
    private static readonly string OtherTestProjectPath = TestProjectPath + "-Other";
    private static readonly string ExtendedOtherTestProjectPath = OperatingSystem.IsWindows()
        ? @"\\?\" + OtherTestProjectPath
        : OtherTestProjectPath;

    private static readonly string ReadyData =
        $$"""
        {
          "state": "ready",
          "projectPath": {{System.Text.Json.JsonSerializer.Serialize(ExtendedTestProjectPath)}},
          "pid": 4321,
          "editor": {
            "isPlaying": false,
            "isPaused": false,
            "isCompiling": false,
            "isUpdating": false,
            "playability": { "reason": "ready" }
          }
        }
        """;

    private static readonly string StoppedData =
        $$"""
        {
          "state": "stopped",
          "projectPath": {{System.Text.Json.JsonSerializer.Serialize(ExtendedTestProjectPath)}},
          "diagnostic": {
            "project": { "unityVersion": "6000.6.0f1" },
            "editor": { "running": false },
            "editorLog": { "safeMode": false },
            "recovery": "Editor is not running"
          }
        }
        """;

    private const string HierarchyData =
        """
        {
          "hierarchy": [{"name":"Main Camera","path":"/Main Camera","isActive":true,"components":["Camera"]}],
          "isPrefabMode": false,
          "objectCount": 1,
          "sceneName": "Bootstrap"
        }
        """;

    private const string PrefabSetActiveWriteInput =
        """{"isActive":true,"dryRun":false,"expectedSha256":"sha256:abc123"}""";

    private const string PrefabSetActiveDryRunInput =
        """{"isActive":true,"dryRun":true,"expectedSha256":"sha256:abc123"}""";

    [Fact]
    public async Task Describe_AdvertisesEditorCapabilitiesWithoutBrandBranches()
    {
        using var kernel = CreateKernel(
            (_, _) => throw new InvalidOperationException("Describe 不应启动进程。"));

        var descriptor = await kernel.DescribeAsync(CancellationToken.None);

        Assert.Equal("akio", descriptor.Id);
        Assert.Equal(ProviderKinds.Editor, descriptor.Kind);
        Assert.Equal(BDToolContract.Version, descriptor.ContractVersion);
        Assert.Equal("0.3.0", descriptor.ImplementationVersion);
        Assert.True(descriptor.Capabilities[CapabilityIds.EditorStatus]);
        Assert.True(descriptor.Capabilities[CapabilityIds.EditorToolRead]);
        Assert.True(descriptor.Capabilities[CapabilityIds.EditorToolWrite]);
        Assert.True(descriptor.Capabilities[CapabilityIds.EditorJob]);
        Assert.True(descriptor.Capabilities[CapabilityIds.EditorCancel]);
    }

    [Fact]
    public async Task GetStatus_MapsRealReadyEditorJson()
    {
        using var kernel = CreateKernel((invocation, _) =>
        {
            Assert.Equal(
                new[]
                {
                    "--output", "json", "--project-path", TestProjectPath,
                    "--timeout-ms", "60000", "editor", "status"
                },
                invocation.Arguments);
            return Task.FromResult(Success(ReadyData));
        });

        var status = await kernel.GetStatusAsync(
            new EditorStatusRequest { ProjectPath = TestProjectPath },
            CancellationToken.None);

        Assert.Equal("ready", status.State);
        Assert.Equal(TestProjectPath, status.ProjectPath);
        Assert.Equal(4321, status.ProcessId);
        Assert.Equal("ready", status.Message);
        Assert.Empty(status.UnityVersion);
    }

    [Fact]
    public async Task GetStatus_MapsRealStoppedEditorJson()
    {
        using var kernel = CreateKernel((_, _) => Task.FromResult(Success(StoppedData)));

        var status = await kernel.GetStatusAsync(
            new EditorStatusRequest { ProjectPath = TestProjectPath },
            CancellationToken.None);

        Assert.Equal("stopped", status.State);
        Assert.Equal("6000.6.0f1", status.UnityVersion);
        Assert.Equal("Editor is not running", status.Message);
        Assert.Null(status.ProcessId);
    }

    [Fact]
    public async Task GetStatus_NormalizesExtendedUncProjectPath()
    {
        const string data =
            """
            {
              "state": "stopped",
              "projectPath": "\\\\?\\UNC\\SERVER\\Share\\Project",
              "diagnostic": {
                "editor": { "running": false },
                "recovery": "Editor is not running"
              }
            }
            """;
        using var kernel = CreateKernel(
            (_, _) => Task.FromResult(Success(data)),
            projectPath: @"\\server\share\Project");

        var status = await kernel.GetStatusAsync(new EditorStatusRequest(), CancellationToken.None);

        Assert.Equal("stopped", status.State);
        Assert.Equal(@"\\server\share\Project", status.ProjectPath);
    }

    [Fact]
    public async Task GetStatus_KeepsSafeModeInStableVocabulary()
    {
        var data =
            $$"""
            {
              "state": "safe_mode",
              "projectPath": {{System.Text.Json.JsonSerializer.Serialize(ExtendedTestProjectPath)}},
              "diagnostic": {
                "project": { "unityVersion": "6000.6.0f1" },
                "editor": { "running": true },
                "editorLog": { "safeMode": true },
                "recovery": "Editor entered safe mode"
              }
            }
            """;
        using var kernel = CreateKernel((_, _) => Task.FromResult(Success(data)));

        var status = await kernel.GetStatusAsync(new EditorStatusRequest(), CancellationToken.None);

        Assert.Equal("safe_mode", status.State);
        Assert.Equal("Editor entered safe mode", status.Message);
        Assert.Null(status.ProcessId);
    }

    [Fact]
    public async Task GetStatus_DoesNotParseStderrAsProtocol()
    {
        using var kernel = CreateKernel((_, _) => Task.FromResult(new UnityCliProcessResult
        {
            ExitCode = 0,
            Stdout = "Editor is still starting",
            Stderr = Envelope(ReadyData)
        }));

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.GetStatusAsync(new EditorStatusRequest(), CancellationToken.None));

        Assert.Equal(ErrorCodes.ExecutionFailed, exception.Code);
        Assert.Equal("invalid_json", exception.Details["reason"]);
        Assert.Equal("True", exception.Details["stderrPresent"]);
        Assert.True(int.Parse(exception.Details["stderrLength"]) > 0);
        Assert.DoesNotContain(
            "Editor is still starting",
            string.Join("\\n", exception.Details.Values),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStatus_NonZeroExitIncludesSanitizedDiagnostics()
    {
        using var kernel = CreateKernel((_, _) => Task.FromResult(new UnityCliProcessResult
        {
            ExitCode = 3,
            Stdout = Envelope(ReadyData),
            Stderr = "fatal: cannot read local project"
        }));

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.GetStatusAsync(new EditorStatusRequest(), CancellationToken.None));

        Assert.Equal(ErrorCodes.ExecutionFailed, exception.Code);
        Assert.Equal("nonzero_exit", exception.Details["reason"]);
        Assert.Equal("3", exception.Details["exitCode"]);
        Assert.Equal("True", exception.Details["stderrPresent"]);
        Assert.Equal("32", exception.Details["stderrLength"]);
        Assert.DoesNotContain("fatal:", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "fatal:",
            string.Join("\\n", exception.Details.Values),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStatus_ReturnsNotFoundForStructuredEditorNotFound()
    {
        using var kernel = CreateKernel((_, _) => Task.FromResult(new UnityCliProcessResult
        {
            ExitCode = 7,
            Stdout =
                """
                {"success":false,"data":{"candidates":[]},"errors":[{"code":"EDITOR_NOT_FOUND","message":"Editor stopped"}]}
                """
        }));

        var status = await kernel.GetStatusAsync(new EditorStatusRequest(), CancellationToken.None);

        Assert.Equal("not-found", status.State);
        Assert.Equal("未找到可连接的 Unity Editor。", status.Message);
    }

    [Fact]
    public async Task GetStatus_MapsTimeoutToStableError()
    {
        using var kernel = CreateKernel((_, _) =>
            Task.FromException<UnityCliProcessResult>(new TimeoutException("timed out")));

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.GetStatusAsync(new EditorStatusRequest(), CancellationToken.None));

        Assert.Equal(ErrorCodes.Timeout, exception.Code);
        Assert.Equal("timeout", exception.Details["reason"]);
    }

    [Fact]
    public async Task GetStatus_PreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var kernel = CreateKernel((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Success(ReadyData));
        });

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.GetStatusAsync(new EditorStatusRequest(), cancellation.Token));

        Assert.Equal(ErrorCodes.Cancelled, exception.Code);
        Assert.Equal("caller_cancelled", exception.Details["reason"]);
    }

    [Fact]
    public async Task GetStatus_RejectsProjectOutsideProviderBinding()
    {
        var calls = 0;
        using var kernel = CreateKernel((_, _) =>
        {
            calls++;
            return Task.FromResult(Success(ReadyData));
        });

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.GetStatusAsync(
            new EditorStatusRequest { ProjectPath = OtherTestProjectPath },
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task GetStatus_RejectsEditorProjectMismatch()
    {
        var otherProjectData =
            $$"""
            {
              "state": "ready",
              "projectPath": {{System.Text.Json.JsonSerializer.Serialize(ExtendedOtherTestProjectPath)}},
              "pid": 4321,
              "editor": { "playability": { "reason": "ready" } }
            }
            """;
        using var kernel = CreateKernel((_, _) => Task.FromResult(Success(otherProjectData)));

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.GetStatusAsync(new EditorStatusRequest(), CancellationToken.None));

        Assert.Equal(ErrorCodes.PreconditionFailed, exception.Code);
        Assert.Equal("project_mismatch", exception.Details["reason"]);
    }

    [Fact]
    public async Task SubmitInspect_ReturnsCompletedTypedProviderOutput()
    {
        UnityCliInvocation? toolCall = null;
        using var kernel = CreateInspectKernel((invocation, _) =>
        {
            toolCall = invocation;
            return Task.FromResult(Success(HierarchyData));
        });

        var handle = await kernel.SubmitAsync(
            new EditorOperationRequest
            {
                ProjectPath = TestProjectPath,
                OperationId = "project.inspect",
                InputJson = """{"includeComponents":true}"""
            },
            CancellationToken.None);
        var result = await WaitForTerminalAsync(kernel, handle);

        Assert.Equal(OperationStatus.Completed, result.Status);
        using var output = System.Text.Json.JsonDocument.Parse(result.OutputJson);
        Assert.Equal("Bootstrap", output.RootElement.GetProperty("sceneName").GetString());
        Assert.NotNull(toolCall);
        Assert.True(HasSequence(
            toolCall!,
            "tool",
            "call",
            "get_hierarchy",
            "--json",
            """{"includeComponents":true}"""));
    }

    [Fact]
    public async Task SubmitProbe_UsesCustomReadOnlyToolAndPreservesInput()
    {
        const string input = """{"probe":"ready"}""";
        UnityCliInvocation? schemaCall = null;
        UnityCliInvocation? toolCall = null;
        using var kernel = CreateReadOnlyToolKernel(
            "bdtool_read_only_probe",
            (invocation, _) =>
            {
                toolCall = invocation;
                return Task.FromResult(Success("""{"probe":"ready"}"""));
            },
            schemaCall: invocation => schemaCall = invocation);

        var handle = await kernel.SubmitAsync(
            new EditorOperationRequest
            {
                ProjectPath = TestProjectPath,
                OperationId = "project.probe",
                InputJson = input
            },
            CancellationToken.None);
        var result = await WaitForTerminalAsync(kernel, handle);

        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.NotNull(schemaCall);
        Assert.True(HasSequence(
            schemaCall!,
            "tool",
            "schema",
            "bdtool_read_only_probe"));
        Assert.NotNull(toolCall);
        Assert.True(HasSequence(
            toolCall!,
            "tool",
            "call",
            "bdtool_read_only_probe",
            "--json",
            input));
    }

    [Fact]
    public async Task SubmitAssetSnapshot_UsesDeclaredReadToolAndPreservesInput()
    {
        const string input = """{"assetPath":"Assets/Test.prefab"}""";
        UnityCliInvocation? schemaCall = null;
        UnityCliInvocation? toolCall = null;
        using var kernel = CreateReadOnlyToolKernel(
            "bdtool_core_read",
            (invocation, _) =>
            {
                toolCall = invocation;
                return Task.FromResult(Success("""{"exists":true}"""));
            },
            schemaCall: invocation => schemaCall = invocation);

        var handle = await kernel.SubmitAsync(
            new EditorOperationRequest
            {
                ProjectPath = TestProjectPath,
                OperationId = "project.asset-snapshot",
                InputJson = input
            },
            CancellationToken.None);
        var result = await WaitForTerminalAsync(kernel, handle);

        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.NotNull(schemaCall);
        Assert.True(HasSequence(schemaCall!, "tool", "schema", "bdtool_core_read"));
        Assert.NotNull(toolCall);
        Assert.True(HasSequence(
            toolCall!,
            "tool",
            "call",
            "bdtool_core_read",
            "--json",
            input));
    }

    [Fact]
    public async Task SubmitPrefabSetActive_DryRunUsesCoreParametersWithoutGlobalCliFlag()
    {
        UnityCliInvocation? schemaCall = null;
        UnityCliInvocation? toolCall = null;
        using var kernel = CreateWriteToolKernel(
            (invocation, _) =>
            {
                toolCall = invocation;
                return Task.FromResult(Success("""{"isActive":true,"dryRun":true,"changed":false}"""));
            },
            schemaCall: invocation => schemaCall = invocation);

        var handle = await kernel.SubmitAsync(
            CreatePrefabSetActiveRequest(
                dryRun: true,
                inputJson: PrefabSetActiveDryRunInput),
            CancellationToken.None);
        var result = await WaitForTerminalAsync(kernel, handle);

        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.NotNull(schemaCall);
        Assert.True(HasSequence(
            schemaCall!,
            "tool",
            "schema",
            "bdtool_core_prefab_set_active"));
        Assert.NotNull(toolCall);
        Assert.True(HasSequence(
            toolCall!,
            "tool",
            "call",
            "bdtool_core_prefab_set_active",
            "--json",
            PrefabSetActiveDryRunInput));
        Assert.DoesNotContain("--dry-run", toolCall!.Arguments);
    }

    [Fact]
    public async Task SubmitPrefabSetActive_RejectsReadOnlyToolSchema()
    {
        var calls = 0;
        using var kernel = CreateWriteToolKernel(
            (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("schema 预检拒绝后不应进入 tool call。");
            },
            schemaMutating: false);

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.SubmitAsync(
                CreatePrefabSetActiveRequest(),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.PreconditionFailed, exception.Code);
        Assert.Equal("non_mutating_tool", exception.Details["reason"]);
        Assert.Equal("true", exception.Details["expectedMutating"]);
        Assert.Equal("false", exception.Details["actualMutating"]);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("project.asset-snapshot", true, false)]
    [InlineData("project.prefab-set-active", false, true)]
    public async Task Submit_RejectsRequestMutatingMismatch(
        string operationId,
        bool requestMutating,
        bool expectedMutating)
    {
        var calls = 0;
        using var kernel = CreateKernel((_, _) =>
        {
            calls++;
            return Task.FromResult(Success("{}"));
        });
        var isWrite = string.Equals(
            operationId,
            "project.prefab-set-active",
            StringComparison.Ordinal);

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.SubmitAsync(
                new EditorOperationRequest
                {
                    ProjectPath = TestProjectPath,
                    OperationId = operationId,
                    Mutating = requestMutating,
                    InputJson = isWrite ? PrefabSetActiveWriteInput : "{}",
                    WriteScope = isWrite
                        ? new List<string> { "Assets/Prefabs" }
                        : new List<string>()
                },
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("mutating_request_mismatch", exception.Details["reason"]);
        Assert.Equal(expectedMutating ? "true" : "false", exception.Details["expectedMutating"]);
        Assert.Equal(requestMutating ? "true" : "false", exception.Details["requestMutating"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SubmitPrefabSetActive_RejectsEmptyWriteScope()
    {
        var calls = 0;
        using var kernel = CreateKernel((_, _) =>
        {
            calls++;
            return Task.FromResult(Success("{}"));
        });

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.SubmitAsync(
                CreatePrefabSetActiveRequest(writeScope: new List<string> { "   " }),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal("write_scope_required", exception.Details["reason"]);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(
        """{"dryRun":false,"expectedSha256":"sha256:abc123"}""",
        "prefab_set_active_is_active_required")]
    [InlineData(
        """{"isActive":true,"dryRun":false}""",
        "prefab_set_active_expected_sha256_required")]
    [InlineData(
        """{"isActive":true,"expectedSha256":"sha256:abc123"}""",
        "prefab_set_active_dry_run_required")]
    [InlineData(
        """{"isActive":"true","dryRun":false,"expectedSha256":"sha256:abc123"}""",
        "prefab_set_active_is_active_required")]
    [InlineData(
        """{"isActive":true,"dryRun":"false","expectedSha256":"sha256:abc123"}""",
        "prefab_set_active_dry_run_required")]
    public async Task SubmitPrefabSetActive_RejectsMissingCoreParameters(
        string inputJson,
        string reason)
    {
        var calls = 0;
        using var kernel = CreateKernel((_, _) =>
        {
            calls++;
            return Task.FromResult(Success("{}"));
        });

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.SubmitAsync(
                CreatePrefabSetActiveRequest(inputJson: inputJson),
                CancellationToken.None));

        Assert.Equal(ErrorCodes.InvalidArgument, exception.Code);
        Assert.Equal(reason, exception.Details["reason"]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SubmitProbe_RejectsMutatingToolSchema()
    {
        using var kernel = CreateProbeSchemaKernel(
            """{"name":"bdtool_read_only_probe","mutating":true,"params_schema":{}}""");

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.SubmitAsync(
                new EditorOperationRequest
                {
                    ProjectPath = TestProjectPath,
                    OperationId = "project.probe"
                },
                CancellationToken.None));

        Assert.Equal(ErrorCodes.PreconditionFailed, exception.Code);
        Assert.Equal("mutating_tool", exception.Details["reason"]);
        Assert.Equal("bdtool_read_only_probe", exception.Details["toolName"]);
    }

    [Fact]
    public async Task SubmitProbe_RejectsToolSchemaWithoutMutatingFlag()
    {
        using var kernel = CreateProbeSchemaKernel(
            """{"name":"bdtool_read_only_probe","params_schema":{}}""");

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.SubmitAsync(
                new EditorOperationRequest
                {
                    ProjectPath = TestProjectPath,
                    OperationId = "project.probe"
                },
                CancellationToken.None));

        Assert.Equal(ErrorCodes.PreconditionFailed, exception.Code);
        Assert.Equal("mutating_flag_missing", exception.Details["reason"]);
        Assert.Equal("bdtool_read_only_probe", exception.Details["toolName"]);
    }

    [Fact]
    public async Task SubmitInspect_MapsTimeoutToStableError()
    {
        using var kernel = CreateInspectKernel((_, _) =>
            Task.FromException<UnityCliProcessResult>(new TimeoutException("timed out")));

        var handle = await kernel.SubmitAsync(
            new EditorOperationRequest
            {
                OperationId = "project.inspect",
                ProjectPath = TestProjectPath
            },
            CancellationToken.None);
        var result = await WaitForTerminalAsync(kernel, handle);

        Assert.Equal(OperationStatus.Failed, result.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorCodes.Timeout, error.Code);
        Assert.Equal("timeout", error.Details["reason"]);
    }

    [Fact]
    public async Task SubmitAssetSnapshot_MapsTimeoutToTimeout()
    {
        using var kernel = CreateReadOnlyToolKernel(
            "bdtool_core_read",
            (_, _) => Task.FromException<UnityCliProcessResult>(
                new TimeoutException("timed out")));

        var handle = await kernel.SubmitAsync(
            new EditorOperationRequest
            {
                OperationId = "project.asset-snapshot",
                ProjectPath = TestProjectPath,
                InputJson = """{"assetPath":"Assets/Test.prefab"}"""
            },
            CancellationToken.None);
        var result = await WaitForTerminalAsync(kernel, handle);

        Assert.Equal(OperationStatus.Failed, result.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorCodes.Timeout, error.Code);
        Assert.Equal("timeout", error.Details["reason"]);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task SubmitPrefabSetActive_MapsTimeoutToUncertain()
    {
        using var kernel = CreateWriteToolKernel((_, _) =>
            Task.FromException<UnityCliProcessResult>(new TimeoutException("timed out")));

        var handle = await kernel.SubmitAsync(
            CreatePrefabSetActiveRequest(),
            CancellationToken.None);
        var result = await WaitForTerminalAsync(kernel, handle);

        Assert.Equal(OperationStatus.Uncertain, result.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorCodes.ExecutionUncertain, error.Code);
        Assert.False(error.Retryable);
        Assert.Equal("timeout", error.Details["reason"]);
        Assert.Equal("possible", error.Details["sideEffectRisk"]);
    }

    [Fact]
    public async Task Cancel_WriteJobAfterStartMapsToUncertain()
    {
        using var kernel = CreateWriteToolKernel(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });

        var handle = await kernel.SubmitAsync(
            CreatePrefabSetActiveRequest(),
            CancellationToken.None);
        await kernel.CancelAsync(handle, CancellationToken.None);
        var result = await WaitForTerminalAsync(kernel, handle);

        Assert.Equal(OperationStatus.Uncertain, result.Status);
        var error = Assert.Single(result.Errors);
        Assert.Equal(ErrorCodes.ExecutionUncertain, error.Code);
        Assert.False(error.Retryable);
        Assert.Equal("possible", error.Details["sideEffectRisk"]);
    }

    [Fact]
    public async Task Submit_MapsProcessStartFailureToEditorNotFound()
    {
        using var kernel = CreateKernel((_, _) =>
            Task.FromException<UnityCliProcessResult>(
                new EditorStartException("启动失败", new InvalidOperationException())));

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.SubmitAsync(
                new EditorOperationRequest
                {
                    OperationId = "project.inspect",
                    ProjectPath = TestProjectPath
                },
                CancellationToken.None));

        Assert.Equal(ErrorCodes.EditorNotFound, exception.Code);
        Assert.Equal("process_start_failed", exception.Details["reason"]);
    }

    [Fact]
    public async Task Cancel_MapsRunningJobToCancelled()
    {
        using var kernel = CreateInspectKernel(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });

        var handle = await kernel.SubmitAsync(
            new EditorOperationRequest
            {
                OperationId = "project.inspect",
                ProjectPath = TestProjectPath
            },
            CancellationToken.None);
        await kernel.CancelAsync(handle, CancellationToken.None);
        var result = await WaitForTerminalAsync(kernel, handle);

        Assert.Equal(OperationStatus.Cancelled, result.Status);
        Assert.Equal(ErrorCodes.Cancelled, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task JobResult_CanBeReadRepeatedlyThenExpires()
    {
        using var kernel = CreateInspectKernel(
            (_, _) => Task.FromResult(Success(HierarchyData)),
            completedJobRetentionMilliseconds: 500);

        var handle = await kernel.SubmitAsync(
            new EditorOperationRequest
            {
                OperationId = "project.inspect",
                ProjectPath = TestProjectPath
            },
            CancellationToken.None);
        var first = await WaitForTerminalAsync(kernel, handle);
        var second = await kernel.GetResultAsync(handle, CancellationToken.None);

        Assert.Equal(OperationStatus.Completed, first.Status);
        Assert.Equal(OperationStatus.Completed, second.Status);
        Assert.Equal(first.OutputJson, second.OutputJson);

        await Task.Delay(900);
        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.GetResultAsync(handle, CancellationToken.None));
        Assert.Equal(ErrorCodes.NotFound, exception.Code);
    }

    [Fact]
    public async Task Submit_RejectsMutatingToolSchema()
    {
        using var kernel = CreateKernel((invocation, _) =>
        {
            if (HasSequence(invocation, "editor", "status"))
            {
                return Task.FromResult(Success(ReadyData));
            }

            if (HasSequence(invocation, "tool", "schema", "get_hierarchy"))
            {
                return Task.FromResult(Success(
                    """{"name":"get_hierarchy","mutating":true,"params_schema":{}}"""));
            }

            throw new InvalidOperationException("mutating schema 不应进入 tool call。");
        });

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.SubmitAsync(
                new EditorOperationRequest
                {
                    OperationId = "project.inspect",
                    ProjectPath = TestProjectPath
                },
                CancellationToken.None));

        Assert.Equal(ErrorCodes.PreconditionFailed, exception.Code);
        Assert.Equal("mutating_tool", exception.Details["reason"]);
    }

    [Fact]
    public async Task Submit_RejectsUnknownOperationWithoutStartingProcess()
    {
        var calls = 0;
        using var kernel = CreateKernel((_, _) =>
        {
            calls++;
            return Task.FromResult(Success("{}"));
        });

        var exception = await Assert.ThrowsAsync<EditorException>(() =>
            kernel.SubmitAsync(
                new EditorOperationRequest
                {
                    OperationId = "project.write",
                    ProjectPath = TestProjectPath
                },
                CancellationToken.None));

        Assert.Equal(ErrorCodes.CapabilityUnsupported, exception.Code);
        Assert.Equal(0, calls);
    }

    private static EditorOperationRequest CreatePrefabSetActiveRequest(
        bool mutating = true,
        bool dryRun = false,
        string inputJson = PrefabSetActiveWriteInput,
        List<string>? writeScope = null) =>
        new()
        {
            ProjectPath = TestProjectPath,
            OperationId = "project.prefab-set-active",
            InputJson = inputJson,
            Mutating = mutating,
            DryRun = dryRun,
            WriteScope = writeScope ?? new List<string> { "Assets/Prefabs" }
        };

    private static AkioEditorKernel CreateInspectKernel(
        Func<UnityCliInvocation, CancellationToken, Task<UnityCliProcessResult>> toolCall,
        int completedJobRetentionMilliseconds = 30_000) =>
        CreateReadOnlyToolKernel(
            "get_hierarchy",
            toolCall,
            completedJobRetentionMilliseconds);

    private static AkioEditorKernel CreateWriteToolKernel(
        Func<UnityCliInvocation, CancellationToken, Task<UnityCliProcessResult>> toolCall,
        bool schemaMutating = true,
        int completedJobRetentionMilliseconds = 30_000,
        Action<UnityCliInvocation>? schemaCall = null) =>
        CreateKernel(
            (invocation, cancellationToken) =>
            {
                if (HasSequence(invocation, "editor", "status"))
                {
                    return Task.FromResult(Success(ReadyData));
                }

                if (HasSequence(invocation, "tool", "schema", "bdtool_core_prefab_set_active"))
                {
                    schemaCall?.Invoke(invocation);
                    return Task.FromResult(Success(System.Text.Json.JsonSerializer.Serialize(
                        new
                        {
                            name = "bdtool_core_prefab_set_active",
                            mutating = schemaMutating,
                            params_schema = new { }
                        })));
                }

                if (HasSequence(invocation, "tool", "call", "bdtool_core_prefab_set_active"))
                {
                    return toolCall(invocation, cancellationToken);
                }

                throw new InvalidOperationException("写工具测试桩收到未知 unity-cli 命令。");
            },
            completedJobRetentionMilliseconds);

    private static AkioEditorKernel CreateReadOnlyToolKernel(
        string toolName,
        Func<UnityCliInvocation, CancellationToken, Task<UnityCliProcessResult>> toolCall,
        int completedJobRetentionMilliseconds = 30_000,
        Action<UnityCliInvocation>? schemaCall = null) =>
        CreateKernel(
            (invocation, cancellationToken) =>
            {
                if (HasSequence(invocation, "editor", "status"))
                {
                    return Task.FromResult(Success(ReadyData));
                }

                if (HasSequence(invocation, "tool", "schema", toolName))
                {
                    schemaCall?.Invoke(invocation);
                    return Task.FromResult(Success(System.Text.Json.JsonSerializer.Serialize(
                        new { name = toolName, mutating = false, params_schema = new { } })));
                }

                if (HasSequence(invocation, "tool", "call", toolName))
                {
                    return toolCall(invocation, cancellationToken);
                }

                throw new InvalidOperationException("测试桩收到未知 unity-cli 命令。");
            },
            completedJobRetentionMilliseconds);

    private static AkioEditorKernel CreateProbeSchemaKernel(string schemaJson) =>
        CreateKernel((invocation, _) =>
        {
            if (HasSequence(invocation, "editor", "status"))
            {
                return Task.FromResult(Success(ReadyData));
            }

            if (HasSequence(invocation, "tool", "schema", "bdtool_read_only_probe"))
            {
                return Task.FromResult(Success(schemaJson));
            }

            throw new InvalidOperationException("schema 预检拒绝后不应进入 tool call。");
        });

    private static AkioEditorKernel CreateKernel(
        Func<UnityCliInvocation, CancellationToken, Task<UnityCliProcessResult>> run,
        int completedJobRetentionMilliseconds = 30_000,
        string? projectPath = null) =>
        new(
            new AkioEditorOptions
            {
                UnityCliPath = @"C:\tools\unity-cli.exe",
                ProjectPath = projectPath ?? TestProjectPath,
                DefaultTimeoutMilliseconds = 60_000,
                CompletedJobRetentionMilliseconds = completedJobRetentionMilliseconds,
                CancelWaitMilliseconds = 2_000
            },
            new StubProcessRunner(run));

    private static bool HasSequence(UnityCliInvocation invocation, params string[] sequence)
    {
        for (var start = 0;
             start <= invocation.Arguments.Count - sequence.Length;
             start++)
        {
            var matches = true;
            for (var offset = 0; offset < sequence.Length; offset++)
            {
                if (!string.Equals(
                        invocation.Arguments[start + offset],
                        sequence[offset],
                        StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static string Envelope(string dataJson) =>
        "{\"success\":true,\"errors\":[],\"warnings\":[],\"data\":" + dataJson + "}";

    private static UnityCliProcessResult Success(string dataJson) => new()
    {
        ExitCode = 0,
        Stdout = Envelope(dataJson)
    };

    private static async Task<EditorOperationResult> WaitForTerminalAsync(
        AkioEditorKernel kernel,
        OperationHandle handle)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await kernel.GetResultAsync(handle, CancellationToken.None);
            if (result.Status is not OperationStatus.Queued
                and not OperationStatus.Running)
            {
                return result;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("测试等待 Editor Job 完成超时。");
    }

    private sealed class StubProcessRunner : IUnityCliProcessRunner
    {
        private readonly Func<UnityCliInvocation, CancellationToken, Task<UnityCliProcessResult>> _run;

        public StubProcessRunner(
            Func<UnityCliInvocation, CancellationToken, Task<UnityCliProcessResult>> run)
        {
            _run = run;
        }

        public Task<UnityCliProcessResult> RunAsync(
            UnityCliInvocation invocation,
            CancellationToken cancellationToken) =>
            _run(invocation, cancellationToken);
    }
}


