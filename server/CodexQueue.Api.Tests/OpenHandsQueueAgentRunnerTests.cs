using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;

namespace CodexQueue.Api.Tests;

public sealed class OpenHandsQueueAgentRunnerTests
{
    [Fact]
    public async Task RunAsync_PropagatesConversationDiscardMetadata()
    {
        const string conversationId = "0123456789abcdef0123456789abcdef";
        var profile = new AiProviderProfile
        {
            Name = "Test Ollama",
            Source = AiProviderSource.Local,
            BaseUrl = "http://ollama.test:11434/v1",
            Enabled = true,
            MaximumConcurrency = 1,
        };
        var request = new CodexRequest
        {
            ProviderProfile = profile,
            ProviderProfileId = profile.Id,
            QueueTab = new QueueTab
            {
                OpenHandsConversationId = conversationId,
            },
            PermissionMode = PermissionMode.FullAccess,
            OpenHandsAlwaysApproveConfirmed = true,
        };
        var run = new CodexRun
        {
            Model = "openai/test-model",
            ExecutionRunner = ExecutionRunner.OpenHandsCli,
        };
        var commandRunner = new StubOpenHandsCommandRunner(
            new OpenHandsCommandResult(
                1,
                "safe output",
                "raw diagnostics",
                "safe preview",
                conversationId,
                ReportedError: true,
                DiscardConversation: true));
        var runner = new OpenHandsQueueAgentRunner(
            commandRunner,
            new HealthyProviderService(profile, run.Model));

        var result = await runner.RunAsync(
            new QueueAgentRunContext(
                request,
                run,
                new TargetMachine
                {
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                },
                "/test/project",
                "perform the task",
                null),
            _ => Task.CompletedTask,
            CancellationToken.None);

        Assert.True(result.DiscardOpenHandsConversation);
        Assert.Equal(conversationId, result.OpenHandsConversationId);
        Assert.Equal(conversationId, commandRunner.ObservedConversationId);
    }

    private sealed class StubOpenHandsCommandRunner(OpenHandsCommandResult result)
        : IOpenHandsCommandRunner
    {
        public string? ObservedConversationId { get; private set; }

        public Task<OpenHandsCommandResult> RunAsync(
            TargetMachine machine,
            string projectPath,
            string model,
            string baseUrl,
            string apiKey,
            int contextWindow,
            string? reasoningEffort,
            string? conversationId,
            string prompt,
            bool alwaysApproveConfirmed,
            Func<string, Task> onOutput,
            CancellationToken cancellationToken)
        {
            ObservedConversationId = conversationId;
            return Task.FromResult(result);
        }

        public Task<OpenHandsMachineCheck> TestMachineAsync(
            TargetMachine machine,
            CancellationToken cancellationToken,
            string? localAiBaseUrl = null,
            string? selectedModel = null) =>
            throw new NotSupportedException();
    }

    private sealed class HealthyProviderService(
        AiProviderProfile profile,
        string model)
        : IAiProviderService
    {
        public AiProviderValidationResult Validate(AiProviderProfile candidate) =>
            new(
                IsValid: ReferenceEquals(profile, candidate),
                NormalizedBaseUrl: profile.BaseUrl,
                NormalizedDefaultModel: null,
                Errors: [],
                ContextWarning: null);

        public AiProviderContextWarning? GetContextWarning(AiProviderProfile candidate) =>
            null;

        public void ApplyHealth(
            AiProviderProfile candidate,
            AiProviderDiscoveryResult result)
        {
        }

        public Task<AiProviderDiscoveryResult> DiscoverModelsAsync(
            AiProviderProfile candidate,
            CancellationToken cancellationToken = default,
            bool forceRefresh = false) =>
            Task.FromResult(
                new AiProviderDiscoveryResult(
                    ProviderHealthStatus.Healthy,
                    DateTimeOffset.UtcNow,
                    [new AiProviderModel(model["openai/".Length..], model)],
                    Error: null,
                    FromCache: false));
    }
}
