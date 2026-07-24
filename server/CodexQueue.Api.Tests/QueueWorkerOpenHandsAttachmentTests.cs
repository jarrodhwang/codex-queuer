using CodexQueue.Api.Data;
using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexQueue.Api.Tests;

public sealed class QueueWorkerOpenHandsAttachmentTests
{
    [Theory]
    [InlineData(false, QueueStatus.Succeeded)]
    [InlineData(true, QueueStatus.Failed)]
    public async Task OpenHandsAttachmentHandling_DoesNotFollowProjectControlDirectorySymlink(
        bool hasLegacyAttachments,
        QueueStatus expectedStatus)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-worker-attachment-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var outsideRoot = Path.Combine(testRoot, "outside");
        var requestId = Guid.NewGuid();
        var outsideRequestDirectory = Path.Combine(
            outsideRoot,
            "attachments",
            requestId.ToString("N"));
        var markerPath = Path.Combine(outsideRequestDirectory, "must-remain.txt");
        var controlDirectoryLink = Path.Combine(projectRoot, ".codex-queue");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(outsideRequestDirectory);
        await File.WriteAllTextAsync(markerPath, "outside project");
        Directory.CreateSymbolicLink(controlDirectoryLink, outsideRoot);

        var databasePath = Path.Combine(testRoot, "queue.db");
        var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<AppDbContext>(builder => builder.UseSqlite("Data Source=" + databasePath))
            .BuildServiceProvider();
        try
        {
            await using (var setupScope = services.CreateAsyncScope())
            {
                var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();
                var machine = new TargetMachine
                {
                    Name = "OpenHands test machine",
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                    WorkingRoot = testRoot,
                };
                var project = new Project
                {
                    Name = "OpenHands test project",
                    Path = projectRoot,
                    Machine = machine,
                };
                var profile = new AiProviderProfile
                {
                    Name = "Test Ollama",
                    Source = AiProviderSource.Local,
                    BaseUrl = "http://ollama.test:11434/v1",
                    MaximumConcurrency = 1,
                    Enabled = true,
                };
                var request = new CodexRequest
                {
                    Id = requestId,
                    Project = project,
                    Machine = machine,
                    ProviderProfile = profile,
                    ExecutionRunner = ExecutionRunner.OpenHandsCli,
                    ExecutionProjectPath = projectRoot,
                    ExecutionMachineUpdatedAt = machine.UpdatedAt,
                    Prompt = "do the task",
                    Model = "openai/test-model",
                    PermissionMode = PermissionMode.FullAccess,
                    OpenHandsAlwaysApproveConfirmed = true,
                    AttachmentsJson = hasLegacyAttachments ? "[]" : null,
                    QueueOrder = 1,
                    Status = QueueStatus.Queued,
                };
                db.Runs.Add(new CodexRun
                {
                    Request = request,
                    Kind = RunKind.Request,
                    Model = request.Model,
                    ExecutionRunner = ExecutionRunner.OpenHandsCli,
                    ProviderProfileId = profile.Id,
                    ProviderProfileName = profile.Name,
                    ProviderSource = profile.Source,
                    Status = QueueStatus.Queued,
                });
                await db.SaveChangesAsync();
            }

            var worker = new QueueWorker(
                services.GetRequiredService<IServiceScopeFactory>(),
                new TargetCommandRunner(NullLogger<TargetCommandRunner>.Instance),
                new SuccessfulOpenHandsResolver(),
                new ProviderConcurrencyGate(),
                NullLogger<QueueWorker>.Instance);
            try
            {
                Assert.True(await worker.KickQueueAsync(CancellationToken.None));
                await WaitUntilAsync(async () =>
                {
                    await using var scope = services.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var status = await db.Requests
                        .Where(request => request.Id == requestId)
                        .Select(request => request.Status)
                        .SingleAsync();
                    return status == expectedStatus
                        && worker.GetDiagnostics().ActiveRequestIds.Count == 0;
                }, TimeSpan.FromSeconds(5));

                Assert.True(File.Exists(markerPath));
            }
            finally
            {
                worker.Dispose();
            }
        }
        finally
        {
            await services.DisposeAsync();
            if (Directory.Exists(controlDirectoryLink))
            {
                Directory.Delete(controlDirectoryLink);
            }
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!await predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Test condition was not reached before the timeout.");
            }

            await Task.Delay(25);
        }
    }

    private sealed class SuccessfulOpenHandsResolver : IQueueAgentRunnerResolver
    {
        private readonly IQueueAgentRunner _runner = new SuccessfulOpenHandsRunner();

        public IQueueAgentRunner Resolve(ExecutionRunner executionRunner)
        {
            Assert.Equal(ExecutionRunner.OpenHandsCli, executionRunner);
            return _runner;
        }
    }

    private sealed class SuccessfulOpenHandsRunner : IQueueAgentRunner
    {
        public ExecutionRunner ExecutionRunner => ExecutionRunner.OpenHandsCli;

        public Task<QueueAgentRunResult> RunAsync(
            QueueAgentRunContext context,
            Func<string, Task> onOutput,
            CancellationToken cancellationToken) =>
            Task.FromResult(new QueueAgentRunResult(
                0,
                "OpenHands task completed.",
                "openhands <safe-preview>",
                OpenHandsConversationId: "0123456789abcdef0123456789abcdef",
                RawDiagnosticOutput: "OpenHands task completed."));
    }
}
