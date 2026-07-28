using System.Security.Cryptography;
using System.Text;
using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Services;

public sealed record QueueAgentRunContext(
    CodexRequest Request,
    CodexRun Run,
    TargetMachine Machine,
    string ProjectPath,
    string Prompt,
    IReadOnlyList<string>? ImagePaths,
    bool StartNewSession = false,
    AiProviderProfile? ProviderProfile = null);

public sealed record QueueAgentRunResult(
    int ExitCode,
    string Output,
    string CommandPreview,
    string? CodexSessionId = null,
    string? LocalCodexSessionId = null,
    string? LocalCodexSessionRouteKey = null)
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
            context.StartNewSession ? null : context.Request.QueueTab?.CodexSessionId,
            context.ImagePaths,
            context.Prompt,
            context.Request.PermissionMode,
            context.Request.InternetSearchEnabled,
            onOutput,
            cancellationToken);
        return new QueueAgentRunResult(
            result.ExitCode,
            result.Output,
            result.CommandPreview,
            CodexSessionId: result.CodexSessionId);
    }
}

public sealed class LocalCodexQueueAgentRunner(
    ITargetCommandRunner targetRunner,
    IAiProviderService providerService)
    : IQueueAgentRunner
{
    public ExecutionRunner ExecutionRunner => ExecutionRunner.OpenHandsCli;

    public async Task<QueueAgentRunResult> RunAsync(
        QueueAgentRunContext context,
        Func<string, Task> onOutput,
        CancellationToken cancellationToken)
    {
        var profile = context.ProviderProfile ?? context.Request.ProviderProfile
            ?? throw new InvalidOperationException("Local Codex request provider profile is unavailable.");
        if (profile.Source != AiProviderSource.Local)
        {
            throw new InvalidOperationException(
                "Local Codex executes only Local AI Server profiles.");
        }

        if (!profile.Enabled)
        {
            throw new InvalidOperationException("Selected Local AI Server profile is disabled.");
        }

        if (!string.IsNullOrWhiteSpace(profile.ApiKeyEnvironmentVariable))
        {
            throw new InvalidOperationException(
                "Authenticated Local AI profiles are not supported in this release. Protect the server with a private LAN or VPN.");
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

        var selectedModel = AiProviderService.FindLocalModel(
            discovery.Models,
            context.Run.Model);
        if (selectedModel is null)
        {
            throw new InvalidOperationException("Selected model is not installed on the Local AI server.");
        }

        if (selectedModel.ToolSupportKnown && !selectedModel.SupportsTools)
        {
            throw new InvalidOperationException(
                selectedModel.Name
                + " does not advertise tool calling support. Local Codex requires a tool-capable model to inspect files, apply changes, and create commits.");
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

        var runtimeModel = await providerService.PrepareModelForContextAsync(
            profile,
            selectedModel.Model,
            contextWindow,
            cancellationToken);
        var sessionRouteKey = BuildSessionRouteKey(
            profile.Id,
            profile.LocalAiServerType,
            validation.NormalizedBaseUrl,
            runtimeModel);
        var queueTab = context.StartNewSession ? null : context.Request.QueueTab;
        var resumableSessionId =
            string.Equals(
                queueTab?.LocalCodexSessionRouteKey,
                sessionRouteKey,
                StringComparison.Ordinal)
                ? queueTab?.LocalCodexSessionId
                : null;
        var result = await targetRunner.RunLocalCodexAsync(
            context.Machine,
            context.ProjectPath,
            profile.LocalAiServerType,
            validation.NormalizedBaseUrl,
            runtimeModel,
            contextWindow,
            context.Run.ModelEffort,
            resumableSessionId,
            context.Prompt,
            context.Request.PermissionMode,
            context.Request.InternetSearchEnabled,
            onOutput,
            cancellationToken);
        return new QueueAgentRunResult(
            result.ExitCode,
            result.Output,
            result.CommandPreview,
            LocalCodexSessionId: result.CodexSessionId,
            LocalCodexSessionRouteKey: sessionRouteKey);
    }

    internal static string BuildSessionRouteKey(
        Guid profileId,
        LocalAiServerType serverType,
        string normalizedBaseUrl,
        string runtimeModel)
    {
        var route = profileId.ToString("N")
            + "\n"
            + serverType
            + "\n"
            + normalizedBaseUrl
            + "\n"
            + runtimeModel;
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(route)))
            .ToLowerInvariant();
    }
}
