using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Services;

public sealed record QueueAgentRunContext(
    CodexRequest Request,
    CodexRun Run,
    TargetMachine Machine,
    string ProjectPath,
    string Prompt,
    IReadOnlyList<string>? ImagePaths);

public sealed record QueueAgentRunResult(
    int ExitCode,
    string Output,
    string CommandPreview,
    string? CodexSessionId = null,
    string? OpenHandsConversationId = null,
    string? RawDiagnosticOutput = null,
    bool DiscardOpenHandsConversation = false)
{
    public bool Success => ExitCode == 0;
}

public interface IQueueAgentRunner
{
    ExecutionRunner ExecutionRunner { get; }

    Task<QueueAgentRunResult> RunAsync(
        QueueAgentRunContext context,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken);
}

public interface IQueueAgentRunnerResolver
{
    IQueueAgentRunner Resolve(ExecutionRunner executionRunner);
}

public sealed class QueueAgentRunnerResolver(IEnumerable<IQueueAgentRunner> runners)
    : IQueueAgentRunnerResolver
{
    private readonly IReadOnlyDictionary<ExecutionRunner, IQueueAgentRunner> _runners =
        runners.ToDictionary(x => x.ExecutionRunner);

    public IQueueAgentRunner Resolve(ExecutionRunner executionRunner) =>
        _runners.TryGetValue(executionRunner, out var runner)
            ? runner
            : throw new InvalidOperationException(
                "No queue agent runner is registered for " + executionRunner + ".");
}

public sealed class CodexQueueAgentRunner(ITargetCommandRunner targetRunner)
    : IQueueAgentRunner
{
    public ExecutionRunner ExecutionRunner => ExecutionRunner.CodexCli;

    public async Task<QueueAgentRunResult> RunAsync(
        QueueAgentRunContext context,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        var result = await targetRunner.RunCodexAsync(
            context.Machine,
            context.ProjectPath,
            context.Run.Model,
            context.Run.ModelEffort,
            context.Run.ModelSpeed,
            context.Request.QueueTab?.CodexSessionId,
            context.ImagePaths,
            context.Prompt,
            context.Request.PermissionMode,
            onOutput,
            cancellationToken);
        return new QueueAgentRunResult(
            result.ExitCode,
            result.Output,
            result.CommandPreview,
            CodexSessionId: result.CodexSessionId);
    }
}

public sealed class OpenHandsQueueAgentRunner(
    IOpenHandsCommandRunner commandRunner,
    IAiProviderService providerService)
    : IQueueAgentRunner
{
    public ExecutionRunner ExecutionRunner => ExecutionRunner.OpenHandsCli;

    public async Task<QueueAgentRunResult> RunAsync(
        QueueAgentRunContext context,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        var profile = context.Request.ProviderProfile
            ?? throw new InvalidOperationException("OpenHands request provider profile is unavailable.");
        if (profile.Source != AiProviderSource.Local)
        {
            throw new InvalidOperationException(
                "This OpenHands release executes only Local/Ollama provider profiles.");
        }

        if (!profile.Enabled)
        {
            throw new InvalidOperationException("Selected Local AI Server profile is disabled.");
        }

        if (!string.IsNullOrWhiteSpace(profile.ApiKeyEnvironmentVariable))
        {
            throw new InvalidOperationException(
                "Authenticated Local AI profiles are not supported in this release because OpenHands child processes can inherit provider credentials.");
        }

        var validation = providerService.Validate(profile);
        if (!validation.IsValid || validation.NormalizedBaseUrl is null)
        {
            throw new InvalidOperationException(
                "Selected Local AI Server profile is invalid: " + string.Join(" ", validation.Errors));
        }

        var discovery = await providerService.DiscoverModelsAsync(profile, cancellationToken);
        if (discovery.HealthStatus != ProviderHealthStatus.Healthy)
        {
            throw new InvalidOperationException(
                "Local AI server is offline or unavailable: " + (discovery.Error ?? "health check failed."));
        }

        var selectedModel = discovery.Models.FirstOrDefault(x =>
            string.Equals(x.Model, context.Run.Model, StringComparison.OrdinalIgnoreCase));
        if (selectedModel is null)
        {
            throw new InvalidOperationException("Selected model is not installed on the Local AI server.");
        }

        var contextWindow = profile.ConfiguredContextWindow ?? AiProviderService.RecommendedContextWindow;
        if (!string.IsNullOrWhiteSpace(context.Run.ModelSpeed))
        {
            if (!int.TryParse(context.Run.ModelSpeed, out var requestedContextWindow)
                || requestedContextWindow < AiProviderService.ContextWarningThreshold
                || requestedContextWindow > contextWindow)
            {
                throw new InvalidOperationException(
                    "The saved Local context size is invalid or exceeds the Local AI server's configured limit.");
            }

            contextWindow = requestedContextWindow;
        }

        if (selectedModel.MaximumContextWindow is { } maximumContextWindow
            && contextWindow > maximumContextWindow)
        {
            throw new InvalidOperationException(
                "The saved Local context size exceeds the selected model's advertised limit.");
        }

        var result = await commandRunner.RunAsync(
            context.Machine,
            context.ProjectPath,
            context.Run.Model,
            validation.NormalizedBaseUrl,
            AiProviderService.LocalPlaceholderApiKey,
            contextWindow,
            context.Run.ModelEffort,
            context.Request.QueueTab?.OpenHandsConversationId,
            context.Prompt,
            context.Request.OpenHandsAlwaysApproveConfirmed,
            onOutput,
            cancellationToken);
        return new QueueAgentRunResult(
            result.ExitCode,
            result.Output,
            result.CommandPreview,
            OpenHandsConversationId: result.ConversationId,
            RawDiagnosticOutput: result.RawDiagnosticOutput,
            DiscardOpenHandsConversation: result.DiscardConversation);
    }
}
