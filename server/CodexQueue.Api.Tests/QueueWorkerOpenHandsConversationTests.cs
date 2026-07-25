using System.Text.Json;
using CodexQueue.Api.Data;
using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexQueue.Api.Tests;

public sealed class QueueWorkerOpenHandsConversationTests
{
    private const string FailedConversationId = "0123456789abcdef0123456789abcdef";
    private const string FreshConversationId = "11111111111111111111111111111111";
    private const string NewerConversationId = "22222222222222222222222222222222";

    [Fact]
    public async Task FreshNoActivityFailure_ClearsTabButKeepsRunDiagnosticId()
    {
        var result = await RunScenarioAsync(
            initialStatus: QueueStatus.Queued,
            initialConversationId: null,
            addPriorSuccessfulConversation: false,
            resume: false,
            runnerResult: new QueueAgentRunResult(
                1,
                NoActivityError(),
                "openhands <safe-preview>",
                OpenHandsConversationId: FailedConversationId,
                RawDiagnosticOutput: "bounded diagnostics",
                DiscardOpenHandsConversation: true));

        Assert.Equal(QueueStatus.Failed, result.Status);
        Assert.Null(result.ObservedConversationId);
        Assert.Null(result.TabConversationId);
        Assert.Equal(FailedConversationId, result.RunConversationId);
        Assert.Equal("bounded diagnostics", result.RawDiagnosticOutput);
    }

    [Fact]
    public async Task StaleDiscardResult_DoesNotReplaceNewerTabConversation()
    {
        var result = await RunScenarioAsync(
            initialStatus: QueueStatus.Queued,
            initialConversationId: NewerConversationId,
            addPriorSuccessfulConversation: false,
            resume: false,
            runnerResult: new QueueAgentRunResult(
                1,
                NoActivityError(),
                "openhands <safe-preview>",
                OpenHandsConversationId: FailedConversationId,
                RawDiagnosticOutput: "bounded diagnostics",
                DiscardOpenHandsConversation: true));

        Assert.Equal(NewerConversationId, result.ObservedConversationId);
        Assert.Equal(NewerConversationId, result.TabConversationId);
        Assert.Equal(FailedConversationId, result.RunConversationId);
    }

    [Fact]
    public async Task NoActivityContinuation_PreservesConversationWithPriorAgentActivity()
    {
        var result = await RunScenarioAsync(
            initialStatus: QueueStatus.Queued,
            initialConversationId: FailedConversationId,
            addPriorSuccessfulConversation: true,
            resume: false,
            runnerResult: new QueueAgentRunResult(
                1,
                NoActivityError(),
                "openhands <safe-preview>",
                OpenHandsConversationId: FailedConversationId,
                DiscardOpenHandsConversation: true));

        Assert.Equal(FailedConversationId, result.ObservedConversationId);
        Assert.Equal(FailedConversationId, result.TabConversationId);
        Assert.Equal(FailedConversationId, result.RunConversationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeNoActivityFailure_DropsOnlyUnestablishedConversation(
        bool addPriorSuccessfulConversation)
    {
        var expectedInputId = addPriorSuccessfulConversation
            ? FailedConversationId
            : null;
        var completedConversationId = expectedInputId ?? FreshConversationId;
        var result = await RunScenarioAsync(
            initialStatus: QueueStatus.Failed,
            initialConversationId: FailedConversationId,
            addPriorSuccessfulConversation,
            resume: true,
            runnerResult: new QueueAgentRunResult(
                0,
                """{"kind":"MessageEvent","source":"agent","message":"Task complete"}""",
                "openhands <safe-preview>",
                OpenHandsConversationId: completedConversationId,
                RawDiagnosticOutput: "bounded diagnostics"));

        Assert.Equal(QueueStatus.Succeeded, result.Status);
        Assert.Equal(expectedInputId, result.ObservedConversationId);
        Assert.Equal(completedConversationId, result.TabConversationId);
        Assert.Equal(completedConversationId, result.RunConversationId);
    }

    [Fact]
    public async Task Resume_DoesNotTrustPlaintextNoActivityMarker()
    {
        var result = await RunScenarioAsync(
            initialStatus: QueueStatus.Failed,
            initialConversationId: FailedConversationId,
            addPriorSuccessfulConversation: false,
            resume: true,
            runnerResult: new QueueAgentRunResult(
                0,
                """{"kind":"MessageEvent","source":"agent","message":"Task complete"}""",
                "openhands <safe-preview>",
                OpenHandsConversationId: FailedConversationId),
            initialRunOutput:
                "Tool output mentioned OpenHandsNoAgentActivity but is not a runner event.");

        Assert.Equal(FailedConversationId, result.ObservedConversationId);
        Assert.Equal(FailedConversationId, result.TabConversationId);
    }

    [Fact]
    public async Task Resume_DoesNotTrustPriorEmptySucceededRun()
    {
        var result = await RunScenarioAsync(
            initialStatus: QueueStatus.Failed,
            initialConversationId: FailedConversationId,
            addPriorSuccessfulConversation: true,
            resume: true,
            runnerResult: new QueueAgentRunResult(
                0,
                AgentActivityEvent(),
                "openhands <safe-preview>",
                OpenHandsConversationId: FreshConversationId),
            priorSuccessfulRunOutput: "");

        Assert.Null(result.ObservedConversationId);
        Assert.Equal(FreshConversationId, result.TabConversationId);
    }

    private static async Task<ScenarioResult> RunScenarioAsync(
        QueueStatus initialStatus,
        string? initialConversationId,
        bool addPriorSuccessfulConversation,
        bool resume,
        QueueAgentRunResult runnerResult,
        string? initialRunOutput = null,
        string? priorSuccessfulRunOutput = null)
    {
        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-worker-conversation-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        Directory.CreateDirectory(projectRoot);
        var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<AppDbContext>(
                builder => builder.UseSqlite(
                    "Data Source=" + Path.Combine(testRoot, "queue.db")))
            .BuildServiceProvider();
        var requestId = Guid.NewGuid();
        var tabId = Guid.NewGuid();
        var runner = new CapturingOpenHandsRunner(runnerResult);
        try
        {
            await using (var setupScope = services.CreateAsyncScope())
            {
                var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();
                var machine = new TargetMachine
                {
                    Name = "OpenHands conversation test machine",
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                    WorkingRoot = testRoot,
                };
                var project = new Project
                {
                    Name = "OpenHands conversation test project",
                    Path = projectRoot,
                    Machine = machine,
                };
                var tab = new QueueTab
                {
                    Id = tabId,
                    Name = "OpenHands conversation test tab",
                    Project = project,
                    OpenHandsConversationId = initialConversationId,
                };
                var profile = new AiProviderProfile
                {
                    Name = "OpenHands conversation test Ollama",
                    Source = AiProviderSource.Local,
                    BaseUrl = "http://ollama.test:11434/v1",
                    MaximumConcurrency = 1,
                    Enabled = true,
                };
                var request = CreateRequest(
                    requestId,
                    initialStatus,
                    initialConversationId,
                    projectRoot,
                    machine,
                    project,
                    tab,
                    profile,
                    initialRunOutput);
                db.Requests.Add(request);

                if (addPriorSuccessfulConversation)
                {
                    var priorRequest = CreateRequest(
                        Guid.NewGuid(),
                        QueueStatus.Succeeded,
                        FailedConversationId,
                        projectRoot,
                        machine,
                        project,
                        tab,
                        profile,
                        initialRunOutput: null);
                    priorRequest.QueueOrder = 0;
                    priorRequest.FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                    var priorRun = priorRequest.Runs.Single();
                    priorRun.Status = QueueStatus.Succeeded;
                    priorRun.FinishedAt = priorRequest.FinishedAt;
                    priorRun.Output =
                        priorSuccessfulRunOutput ?? AgentActivityEvent();
                    db.Requests.Add(priorRequest);
                }

                await db.SaveChangesAsync();
            }

            using var worker = new QueueWorker(
                services.GetRequiredService<IServiceScopeFactory>(),
                new TargetCommandRunner(NullLogger<TargetCommandRunner>.Instance),
                new SingleRunnerResolver(runner),
                new ProviderConcurrencyGate(),
                NullLogger<QueueWorker>.Instance);
            var dispatched = resume
                ? await worker.ResumeRequestAsync(requestId, CancellationToken.None)
                : await worker.KickQueueAsync(CancellationToken.None);
            Assert.True(dispatched);

            await WaitUntilAsync(async () =>
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var status = await db.Requests
                    .Where(request => request.Id == requestId)
                    .Select(request => request.Status)
                    .SingleAsync();
                return status is QueueStatus.Succeeded or QueueStatus.Failed
                    && worker.GetDiagnostics().ActiveRequestIds.Count == 0;
            });

            await using var verificationScope = services.CreateAsyncScope();
            var verificationDb =
                verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persistedRequest = await verificationDb.Requests
                .Include(request => request.QueueTab)
                .Include(request => request.Runs)
                .SingleAsync(request => request.Id == requestId);
            var persistedRun = persistedRequest.Runs.Single();
            return new ScenarioResult(
                persistedRequest.Status,
                runner.ObservedConversationId,
                persistedRequest.QueueTab?.OpenHandsConversationId,
                persistedRun.OpenHandsConversationId,
                persistedRun.RawDiagnosticOutput);
        }
        finally
        {
            await services.DisposeAsync();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static CodexRequest CreateRequest(
        Guid requestId,
        QueueStatus status,
        string? conversationId,
        string projectRoot,
        TargetMachine machine,
        Project project,
        QueueTab tab,
        AiProviderProfile profile,
        string? initialRunOutput)
    {
        var request = new CodexRequest
        {
            Id = requestId,
            Project = project,
            QueueTab = tab,
            Machine = machine,
            ProviderProfile = profile,
            ExecutionRunner = ExecutionRunner.OpenHandsCli,
            ExecutionProjectPath = projectRoot,
            ExecutionMachineUpdatedAt = machine.UpdatedAt,
            Prompt = "perform the test task",
            Model = "openai/test-model",
            PermissionMode = PermissionMode.FullAccess,
            OpenHandsAlwaysApproveConfirmed = true,
            QueueOrder = 1,
            Status = status,
            StartedAt = status == QueueStatus.Failed
                ? DateTimeOffset.UtcNow.AddMinutes(-1)
                : null,
            FinishedAt = status == QueueStatus.Failed
                ? DateTimeOffset.UtcNow
                : null,
            Error = status == QueueStatus.Failed
                ? "OpenHands did not process the task."
                : null,
        };
        request.Runs.Add(new CodexRun
        {
            Request = request,
            Kind = RunKind.Request,
            Model = request.Model,
            ExecutionRunner = ExecutionRunner.OpenHandsCli,
            ProviderProfileId = profile.Id,
            ProviderProfileName = profile.Name,
            ProviderSource = profile.Source,
            Status = status,
            StartedAt = request.StartedAt,
            FinishedAt = request.FinishedAt,
            OpenHandsConversationId = conversationId,
            Output = status == QueueStatus.Failed
                ? initialRunOutput ?? NoActivityError()
                : "",
            Error = request.Error,
        });
        return request;
    }

    private static string NoActivityError() =>
        JsonSerializer.Serialize(new
        {
            kind = "ConversationErrorEvent",
            code = OpenHandsCommandRunner.NoAgentActivityErrorCode,
            message = "OpenHands did not process the task.",
        })
        + Environment.NewLine;

    private static string AgentActivityEvent() =>
        """{"kind":"MessageEvent","source":"agent","message":"Task complete"}"""
        + Environment.NewLine;

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!await predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "OpenHands conversation test did not finish before the timeout.");
            }

            await Task.Delay(25);
        }
    }

    private sealed class SingleRunnerResolver(IQueueAgentRunner runner)
        : IQueueAgentRunnerResolver
    {
        public IQueueAgentRunner Resolve(ExecutionRunner executionRunner)
        {
            Assert.Equal(ExecutionRunner.OpenHandsCli, executionRunner);
            return runner;
        }
    }

    private sealed class CapturingOpenHandsRunner(QueueAgentRunResult result)
        : IQueueAgentRunner
    {
        public string? ObservedConversationId { get; private set; }

        public ExecutionRunner ExecutionRunner => ExecutionRunner.OpenHandsCli;

        public async Task<QueueAgentRunResult> RunAsync(
            QueueAgentRunContext context,
            Func<string, Task> onOutput,
            CancellationToken cancellationToken)
        {
            ObservedConversationId =
                context.Request.QueueTab?.OpenHandsConversationId;
            await onOutput(result.Output + Environment.NewLine);
            return result;
        }
    }

    private sealed record ScenarioResult(
        QueueStatus Status,
        string? ObservedConversationId,
        string? TabConversationId,
        string? RunConversationId,
        string? RawDiagnosticOutput);
}
