using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;

namespace CodexQueue.Api.Tests;

public sealed class LocalCodexQueueAgentRunnerTests
{
    [Fact]
    public async Task StandardCodexRun_UsesOnlyStandardSessionState()
    {
        const string priorCloudSessionId = "prior-cloud-session";
        const string nextCloudSessionId = "next-cloud-session";
        var targetRunner = new RecordingTargetCommandRunner(
            cloudResult: new CommandResult(
                0,
                "cloud done",
                "codex exec <cloud>",
                nextCloudSessionId));
        var runner = new CodexQueueAgentRunner(targetRunner);
        var request = new CodexRequest
        {
            QueueTab = new QueueTab
            {
                CodexSessionId = priorCloudSessionId,
                LocalCodexSessionId = "must-not-be-used",
                LocalCodexSessionRouteKey = "must-not-be-used",
            },
            PermissionMode = PermissionMode.ApproveForMe,
        };
        var run = new CodexRun
        {
            Model = "gpt-5.6",
            ModelEffort = "high",
            ModelSpeed = "priority",
            ExecutionRunner = ExecutionRunner.CodexCli,
        };
        var machine = new TargetMachine
        {
            Kind = MachineKind.Local,
            Platform = MachinePlatform.Linux,
        };
        var imagePaths = new[] { "/workspace/image.png" };

        var result = await runner.RunAsync(
            new QueueAgentRunContext(
                request,
                run,
                machine,
                "/workspace/project",
                "perform the cloud task",
                imagePaths),
            _ => Task.CompletedTask,
            CancellationToken.None);

        var invocation =
            Assert.IsType<CloudInvocation>(targetRunner.CloudInvocation);
        Assert.Same(machine, invocation.Machine);
        Assert.Equal(priorCloudSessionId, invocation.CodexSessionId);
        Assert.Equal(imagePaths, invocation.ImagePaths);
        Assert.Equal(nextCloudSessionId, result.CodexSessionId);
        Assert.Null(result.LocalCodexSessionId);
        Assert.Null(targetRunner.Invocation);
    }

    [Theory]
    [InlineData(LocalAiServerType.Ollama)]
    [InlineData(LocalAiServerType.LmStudio)]
    [InlineData(LocalAiServerType.LlamaCpp)]
    public async Task RunAsync_PropagatesSavedLocalSettingsAndSession(
        LocalAiServerType serverType)
    {
        const string selectedModel = "openai/foo";
        const string priorSessionId = "prior-local-session";
        const string nextSessionId = "next-local-session";
        const string normalizedBaseUrl = "http://local-ai.test:8080/v1";
        var profile = LocalProfile(serverType);
        const int expectedContextWindow = 4_096;
        var targetRunner = new RecordingTargetCommandRunner(
            new CommandResult(
                0,
                """{"type":"thread.started","thread_id":"next-local-session"}""",
                "codex exec <local>",
                nextSessionId));
        var providerService = new StubProviderService(
            profile,
            normalizedBaseUrl,
            new AiProviderDiscoveryResult(
                ProviderHealthStatus.Healthy,
                DateTimeOffset.UtcNow,
                [
                    new AiProviderModel("foo", "foo", 131_072),
                    new AiProviderModel(selectedModel, selectedModel, 131_072),
                ],
                Error: null,
                FromCache: false));
        var runner = new LocalCodexQueueAgentRunner(
            targetRunner,
            providerService);
        var context = CreateContext(
            profile,
            selectedModel,
            modelEffort: "high",
            contextWindow: expectedContextWindow.ToString(),
            priorSessionId,
            PermissionMode.FullAccess);
        var streamed = new List<string>();

        var result = await runner.RunAsync(
            context,
            chunk =>
            {
                streamed.Add(chunk);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        var invocation = Assert.IsType<LocalInvocation>(targetRunner.Invocation);
        Assert.Same(context.Machine, invocation.Machine);
        Assert.Equal(context.ProjectPath, invocation.ProjectPath);
        Assert.Equal(serverType, invocation.ServerType);
        Assert.Equal(normalizedBaseUrl, invocation.BaseUrl);
        Assert.Equal(selectedModel, invocation.Model);
        Assert.Equal(expectedContextWindow, invocation.ContextWindow);
        Assert.Equal("high", invocation.ModelEffort);
        Assert.Equal(priorSessionId, invocation.CodexSessionId);
        Assert.Equal(context.Prompt, invocation.Prompt);
        Assert.Equal(PermissionMode.FullAccess, invocation.PermissionMode);
        Assert.Equal(nextSessionId, result.LocalCodexSessionId);
        Assert.Equal(
            LocalCodexQueueAgentRunner.BuildSessionRouteKey(
                profile.Id,
                serverType,
                normalizedBaseUrl,
                selectedModel),
            result.LocalCodexSessionRouteKey);
        Assert.Null(result.CodexSessionId);
        Assert.Equal(
            [$"Using Local context size: {expectedContextWindow:N0} tokens." + Environment.NewLine, "target-stream"],
            streamed);
    }

    [Fact]
    public async Task RunAsync_UsesRawCatalogModelForLegacySyntheticPrefix()
    {
        var profile = LocalProfile(LocalAiServerType.Ollama);
        var targetRunner = new RecordingTargetCommandRunner();
        var providerService = new StubProviderService(
            profile,
            "http://local-ai.test:8080/v1",
            new AiProviderDiscoveryResult(
                ProviderHealthStatus.Healthy,
                DateTimeOffset.UtcNow,
                [new AiProviderModel("foo", "foo", 131_072)],
                Error: null,
                FromCache: false));
        var runner = new LocalCodexQueueAgentRunner(
            targetRunner,
            providerService);

        await runner.RunAsync(
            CreateContext(
                profile,
                model: "openai/foo",
                modelEffort: "medium",
                contextWindow: "65536",
                sessionId: null,
                PermissionMode.FullAccess),
            _ => Task.CompletedTask,
            CancellationToken.None);

        var invocation = Assert.IsType<LocalInvocation>(targetRunner.Invocation);
        Assert.Equal("foo", invocation.Model);
    }

    [Fact]
    public async Task RunAsync_AllowsOllamaModelThatAdvertisesNoToolCalling()
    {
        var profile = LocalProfile(LocalAiServerType.Ollama);
        var targetRunner = new RecordingTargetCommandRunner();
        var providerService = new StubProviderService(
            profile,
            "http://local-ai.test:8080/v1",
            new AiProviderDiscoveryResult(
                ProviderHealthStatus.Healthy,
                DateTimeOffset.UtcNow,
                [
                    new AiProviderModel(
                        "gemma3:4b",
                        "gemma3:4b",
                        131_072,
                        SupportsTools: false,
                        ToolSupportKnown: true)
                ],
                Error: null,
                FromCache: false));
        var runner = new LocalCodexQueueAgentRunner(
            targetRunner,
            providerService);

        await runner.RunAsync(
            CreateContext(
                profile,
                model: "gemma3:4b",
                modelEffort: null,
                contextWindow: "65536",
                sessionId: null,
                PermissionMode.FullAccess),
            _ => Task.CompletedTask,
            CancellationToken.None);

        var invocation = Assert.IsType<LocalInvocation>(targetRunner.Invocation);
        Assert.Equal("gemma3:4b", invocation.Model);
    }

    [Fact]
    public async Task RunAsync_DoesNotResumeSessionBoundToAnotherProviderRoute()
    {
        var profile = LocalProfile(LocalAiServerType.LmStudio);
        var targetRunner = new RecordingTargetCommandRunner();
        var runner = new LocalCodexQueueAgentRunner(
            targetRunner,
            HealthyProviderService(profile));
        var context = CreateContext(
            profile,
            model: "foo",
            modelEffort: null,
            contextWindow: "32768",
            sessionId: "session-from-another-server",
            PermissionMode.FullAccess);
        context.Request.QueueTab!.LocalCodexSessionRouteKey =
            LocalCodexQueueAgentRunner.BuildSessionRouteKey(
                profile.Id,
                LocalAiServerType.Ollama,
                "http://another-server.test:11434/v1",
                "foo");

        await runner.RunAsync(
            context,
            _ => Task.CompletedTask,
            CancellationToken.None);

        var invocation = Assert.IsType<LocalInvocation>(targetRunner.Invocation);
        Assert.Null(invocation.CodexSessionId);
    }

    [Fact]
    public async Task RunAsync_RejectsMissingProfileBeforeTargetExecution()
    {
        var targetRunner = new RecordingTargetCommandRunner();
        var fallbackProfile = LocalProfile(LocalAiServerType.Ollama);
        var runner = new LocalCodexQueueAgentRunner(
            targetRunner,
            HealthyProviderService(fallbackProfile));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(
                CreateContext(
                    profile: null,
                    model: "foo",
                    modelEffort: null,
                    contextWindow: null,
                    sessionId: null,
                    PermissionMode.ReadOnly),
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Contains(
            "provider profile is unavailable",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(targetRunner.Invocation);
    }

    [Theory]
    [InlineData(
        AiProviderSource.OpenAi,
        true,
        ProviderHealthStatus.Healthy,
        "only Local AI Server profiles")]
    [InlineData(
        AiProviderSource.Local,
        false,
        ProviderHealthStatus.Healthy,
        "profile is disabled")]
    [InlineData(
        AiProviderSource.Local,
        true,
        ProviderHealthStatus.Offline,
        "offline or unavailable")]
    public async Task RunAsync_RejectsWrongDisabledOrOfflineProfileBeforeTargetExecution(
        AiProviderSource source,
        bool enabled,
        ProviderHealthStatus healthStatus,
        string expectedError)
    {
        var profile = LocalProfile(LocalAiServerType.LmStudio);
        profile.Source = source;
        profile.Enabled = enabled;
        var targetRunner = new RecordingTargetCommandRunner();
        var providerService = new StubProviderService(
            profile,
            "http://local-ai.test:8080/v1",
            new AiProviderDiscoveryResult(
                healthStatus,
                DateTimeOffset.UtcNow,
                healthStatus == ProviderHealthStatus.Healthy
                    ? [new AiProviderModel("foo", "foo")]
                    : [],
                healthStatus == ProviderHealthStatus.Offline
                    ? "connection refused"
                    : null,
                FromCache: false));
        var runner = new LocalCodexQueueAgentRunner(
            targetRunner,
            providerService);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(
                CreateContext(
                    profile,
                    model: "foo",
                    modelEffort: null,
                    contextWindow: "32768",
                    sessionId: null,
                    PermissionMode.ReadOnly),
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Contains(
            expectedError,
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(targetRunner.Invocation);
    }

    private static StubProviderService HealthyProviderService(
        AiProviderProfile profile) =>
        new(
            profile,
            "http://local-ai.test:8080/v1",
            new AiProviderDiscoveryResult(
                ProviderHealthStatus.Healthy,
                DateTimeOffset.UtcNow,
                [new AiProviderModel("foo", "foo", 131_072)],
                Error: null,
                FromCache: false));

    private static AiProviderProfile LocalProfile(
        LocalAiServerType serverType) =>
        new()
        {
            Name = "Test Local AI",
            Source = AiProviderSource.Local,
            LocalAiServerType = serverType,
            BaseUrl = "http://local-ai.test:8080/",
            ModelDiscoveryMode = serverType == LocalAiServerType.Ollama
                ? ModelDiscoveryMode.Ollama
                : ModelDiscoveryMode.OpenAi,
            Enabled = true,
            MaximumConcurrency = 1,
            ConfiguredContextWindow = 65_536,
        };

    private static QueueAgentRunContext CreateContext(
        AiProviderProfile? profile,
        string model,
        string? modelEffort,
        string? contextWindow,
        string? sessionId,
        PermissionMode permissionMode)
    {
        var request = new CodexRequest
        {
            ProviderProfile = profile,
            ProviderProfileId = profile?.Id,
            QueueTab = new QueueTab
            {
                LocalCodexSessionId = sessionId,
                LocalCodexSessionRouteKey =
                    profile is null || string.IsNullOrWhiteSpace(sessionId)
                        ? null
                        : LocalCodexQueueAgentRunner.BuildSessionRouteKey(
                            profile.Id,
                            profile.LocalAiServerType,
                            "http://local-ai.test:8080/v1",
                            model),
            },
            PermissionMode = permissionMode,
        };
        var run = new CodexRun
        {
            Model = model,
            ModelEffort = modelEffort,
            ModelSpeed = contextWindow,
            ExecutionRunner = ExecutionRunner.OpenHandsCli,
        };
        return new QueueAgentRunContext(
            request,
            run,
            new TargetMachine
            {
                Kind = MachineKind.Local,
                Platform = MachinePlatform.Linux,
            },
            "/workspace/project",
            "perform the local task; prompt-marker-a13f",
            ImagePaths: null);
    }

    private sealed class StubProviderService(
        AiProviderProfile expectedProfile,
        string normalizedBaseUrl,
        AiProviderDiscoveryResult discovery)
        : IAiProviderService
    {
        public AiProviderValidationResult Validate(AiProviderProfile profile) =>
            new(
                IsValid: ReferenceEquals(expectedProfile, profile),
                NormalizedBaseUrl: normalizedBaseUrl,
                NormalizedDefaultModel: null,
                Errors: [],
                ContextWarning: null);

        public AiProviderContextWarning? GetContextWarning(AiProviderProfile profile) =>
            null;

        public void ApplyHealth(
            AiProviderProfile profile,
            AiProviderDiscoveryResult result)
        {
        }

        public Task<AiProviderDiscoveryResult> DiscoverModelsAsync(
            AiProviderProfile profile,
            CancellationToken cancellationToken = default,
            bool forceRefresh = false)
        {
            Assert.Same(expectedProfile, profile);
            return Task.FromResult(discovery);
        }

        public Task<string> PrepareModelForContextAsync(
            AiProviderProfile profile,
            string model,
            int contextWindow,
            CancellationToken cancellationToken = default)
        {
            Assert.Same(expectedProfile, profile);
            return Task.FromResult(model);
        }
    }

    private sealed class RecordingTargetCommandRunner(
        CommandResult? localResult = null,
        CommandResult? cloudResult = null)
        : ITargetCommandRunner
    {
        private readonly CommandResult _localResult =
            localResult ?? new CommandResult(0, "done", "codex exec <local>");
        private readonly CommandResult _cloudResult =
            cloudResult ?? new CommandResult(0, "done", "codex exec <cloud>");

        public LocalInvocation? Invocation { get; private set; }
        public CloudInvocation? CloudInvocation { get; private set; }

        public Task<CommandResult> RunLocalCodexAsync(
            TargetMachine machine,
            string projectPath,
            LocalAiServerType serverType,
            string baseUrl,
            string model,
            int contextWindow,
            string? modelEffort,
            string? codexSessionId,
            string prompt,
            PermissionMode permissionMode,
            bool internetSearchEnabled,
            Func<string, Task> onOutput,
            CancellationToken cancellationToken)
        {
            Invocation = new LocalInvocation(
                machine,
                projectPath,
                serverType,
                baseUrl,
                model,
                contextWindow,
                modelEffort,
                codexSessionId,
                prompt,
                permissionMode);
            return CompleteAsync();

            async Task<CommandResult> CompleteAsync()
            {
                await onOutput("target-stream");
                return _localResult;
            }
        }

        public Task<CommandResult> ReadRateLimitsAsync(
            TargetMachine machine,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CommandResult> RunCodexAsync(
            TargetMachine machine,
            string projectPath,
            string model,
            string? modelEffort,
            string? modelSpeed,
            string? codexSessionId,
            IReadOnlyList<string>? imagePaths,
            string prompt,
            PermissionMode permissionMode,
            bool internetSearchEnabled,
            Func<string, Task> onOutput,
            CancellationToken cancellationToken)
        {
            CloudInvocation = new CloudInvocation(
                machine,
                projectPath,
                model,
                modelEffort,
                modelSpeed,
                codexSessionId,
                imagePaths,
                prompt,
                permissionMode);
            return Task.FromResult(_cloudResult);
        }

        public Task<LocalCodexMachineCheck> TestLocalCodexAsync(
            TargetMachine machine,
            CancellationToken cancellationToken,
            string? localAiBaseUrl = null,
            string? selectedModel = null) =>
            throw new NotSupportedException();

        public Task WriteAttachmentAsync(
            TargetMachine machine,
            string targetPath,
            byte[] content,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAttachmentDirectoryAsync(
            TargetMachine machine,
            string directoryPath,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CommandResult> RunShellAsync(
            TargetMachine machine,
            string projectPath,
            string shellCommand,
            Func<string, Task> onOutput,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CommandResult> TestMachineAsync(
            TargetMachine machine,
            Func<string, Task> onOutput,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record LocalInvocation(
        TargetMachine Machine,
        string ProjectPath,
        LocalAiServerType ServerType,
        string BaseUrl,
        string Model,
        int ContextWindow,
        string? ModelEffort,
        string? CodexSessionId,
        string Prompt,
        PermissionMode PermissionMode);

    private sealed record CloudInvocation(
        TargetMachine Machine,
        string ProjectPath,
        string Model,
        string? ModelEffort,
        string? ModelSpeed,
        string? CodexSessionId,
        IReadOnlyList<string>? ImagePaths,
        string Prompt,
        PermissionMode PermissionMode);
}
