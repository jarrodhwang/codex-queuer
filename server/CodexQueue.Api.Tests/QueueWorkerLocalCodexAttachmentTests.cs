using CodexQueue.Api.Data;
using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexQueue.Api.Tests;

public sealed class QueueWorkerLocalCodexAttachmentTests
{
    private const string SavedSessionId = "0123456789abcdef0123456789abcdef";
    private const string SavedRouteKey =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(false, QueueStatus.Succeeded)]
    [InlineData(true, QueueStatus.Failed)]
    public async Task LocalCodexAttachmentHandling_DoesNotFollowProjectControlDirectorySymlink(
        bool hasLegacyAttachments,
        QueueStatus expectedStatus)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "local-codex-worker-attachment-tests",
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
                    Name = "Local Codex test machine",
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                    WorkingRoot = testRoot,
                };
                var project = new Project
                {
                    Name = "Local Codex test project",
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
                var queueTab = new QueueTab
                {
                    Project = project,
                    Name = "Local Codex session test",
                };
                var request = new CodexRequest
                {
                    Id = requestId,
                    Project = project,
                    Machine = machine,
                    QueueTab = queueTab,
                    ProviderProfile = profile,
                    ExecutionRunner = ExecutionRunner.OpenHandsCli,
                    ExecutionProjectPath = projectRoot,
                    ExecutionMachineUpdatedAt = machine.UpdatedAt,
                    Prompt = "do the task",
                    Model = "test-model",
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
                new SuccessfulLocalCodexResolver(),
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

                await using var verificationScope = services.CreateAsyncScope();
                var verificationDb =
                    verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var persisted = await verificationDb.Requests
                    .Include(item => item.QueueTab)
                    .Include(item => item.Runs)
                    .SingleAsync(item => item.Id == requestId);
                var persistedRun = Assert.Single(persisted.Runs);
                if (expectedStatus == QueueStatus.Succeeded)
                {
                    Assert.Equal(SavedSessionId, persistedRun.LocalCodexSessionId);
                    Assert.Equal(SavedSessionId, persisted.QueueTab?.LocalCodexSessionId);
                    Assert.Equal(
                        SavedRouteKey,
                        persisted.QueueTab?.LocalCodexSessionRouteKey);
                    Assert.Null(persistedRun.CodexSessionId);
                    Assert.Null(persisted.QueueTab?.CodexSessionId);
                }
                else
                {
                    Assert.Null(persistedRun.LocalCodexSessionId);
                    Assert.Null(persisted.QueueTab?.LocalCodexSessionId);
                }
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

    private sealed class SuccessfulLocalCodexResolver : IQueueAgentRunnerResolver
    {
        private readonly IQueueAgentRunner _runner = new SuccessfulLocalCodexRunner();

        public IQueueAgentRunner Resolve(ExecutionRunner executionRunner)
        {
            Assert.Equal(ExecutionRunner.OpenHandsCli, executionRunner);
            return _runner;
        }
    }

    private sealed class SuccessfulLocalCodexRunner : IQueueAgentRunner
    {
        public ExecutionRunner ExecutionRunner => ExecutionRunner.OpenHandsCli;

        public Task<QueueAgentRunResult> RunAsync(
            QueueAgentRunContext context,
            Func<string, Task> onOutput,
            CancellationToken cancellationToken) =>
            Task.FromResult(new QueueAgentRunResult(
                0,
                "Local Codex task completed.",
                "codex exec <safe-preview>",
                LocalCodexSessionId: SavedSessionId,
                LocalCodexSessionRouteKey: SavedRouteKey));
    }
}
