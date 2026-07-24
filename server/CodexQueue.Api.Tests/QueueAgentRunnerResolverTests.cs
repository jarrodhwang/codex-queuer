using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;

namespace CodexQueue.Api.Tests;

public sealed class QueueAgentRunnerResolverTests
{
    [Fact]
    public void Resolve_DefaultExecutionRunnerSelectsCodex()
    {
        var codex = new StubRunner(ExecutionRunner.CodexCli);
        var openHands = new StubRunner(ExecutionRunner.OpenHandsCli);
        var resolver = new QueueAgentRunnerResolver([codex, openHands]);

        var selected = resolver.Resolve(default);

        Assert.Same(codex, selected);
        Assert.Equal(ExecutionRunner.CodexCli, selected.ExecutionRunner);
    }

    [Fact]
    public void Resolve_ThrowsAnActionableErrorForMissingRunner()
    {
        var resolver = new QueueAgentRunnerResolver(
            [new StubRunner(ExecutionRunner.CodexCli)]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => resolver.Resolve(ExecutionRunner.OpenHandsCli));

        Assert.Contains("OpenHandsCli", exception.Message, StringComparison.Ordinal);
    }

    private sealed class StubRunner(ExecutionRunner executionRunner) : IQueueAgentRunner
    {
        public ExecutionRunner ExecutionRunner { get; } = executionRunner;

        public Task<QueueAgentRunResult> RunAsync(
            QueueAgentRunContext context,
            Func<string, Task> onOutput,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The resolver tests never execute a runner.");
    }
}
