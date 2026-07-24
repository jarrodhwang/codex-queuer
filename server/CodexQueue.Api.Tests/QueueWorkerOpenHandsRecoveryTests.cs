using CodexQueue.Api.Data;
using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexQueue.Api.Tests;

public sealed class QueueWorkerOpenHandsRecoveryTests
{
    [Fact]
    public void FailClosedInterruptedOpenHandsRequests_PausesLocalWorkWithoutChangingCodex()
    {
        var interrupted = CreateRequest(ExecutionRunner.OpenHandsCli, QueueStatus.Running, 1);
        var waitingOpenHands = CreateRequest(ExecutionRunner.OpenHandsCli, QueueStatus.Queued, 2);
        var waitingCodex = CreateRequest(ExecutionRunner.CodexCli, QueueStatus.Queued, 3);

        var changed = QueueWorker.FailClosedInterruptedOpenHandsRequests(
            [interrupted, waitingOpenHands, waitingCodex],
            new HashSet<Guid>());

        Assert.True(changed);
        Assert.Equal(QueueStatus.Failed, interrupted.Status);
        Assert.Contains("orphaned OpenHands process", interrupted.Error, StringComparison.Ordinal);
        Assert.All(interrupted.Runs, run => Assert.Equal(QueueStatus.Failed, run.Status));
        Assert.Equal(QueueStatus.Failed, waitingOpenHands.Status);
        Assert.Contains("then resume this request", waitingOpenHands.Error, StringComparison.Ordinal);
        Assert.All(waitingOpenHands.Runs, run => Assert.Equal(QueueStatus.Failed, run.Status));
        Assert.Equal(QueueStatus.Queued, waitingCodex.Status);
        Assert.All(waitingCodex.Runs, run => Assert.Equal(QueueStatus.Queued, run.Status));
    }

    [Fact]
    public void FailClosedInterruptedOpenHandsRequests_DoesNotTouchAnActiveRun()
    {
        var active = CreateRequest(ExecutionRunner.OpenHandsCli, QueueStatus.Running, 1);
        var waiting = CreateRequest(ExecutionRunner.OpenHandsCli, QueueStatus.Queued, 2);

        var changed = QueueWorker.FailClosedInterruptedOpenHandsRequests(
            [active, waiting],
            new HashSet<Guid> { active.Id });

        Assert.False(changed);
        Assert.Equal(QueueStatus.Running, active.Status);
        Assert.Equal(QueueStatus.Queued, waiting.Status);
    }

    [Fact]
    public void FailClosedInterruptedOpenHandsRequests_PausesWorkForUnownedCancellation()
    {
        var cancelRequested = CreateRequest(
            ExecutionRunner.OpenHandsCli,
            QueueStatus.CancelRequested,
            1);
        var waiting = CreateRequest(ExecutionRunner.OpenHandsCli, QueueStatus.Queued, 2);

        var changed = QueueWorker.FailClosedInterruptedOpenHandsRequests(
            [cancelRequested, waiting],
            new HashSet<Guid>());

        Assert.True(changed);
        Assert.Equal(QueueStatus.Failed, cancelRequested.Status);
        Assert.Contains("orphaned OpenHands process", cancelRequested.Error, StringComparison.Ordinal);
        Assert.Equal(QueueStatus.Failed, waiting.Status);
        Assert.Contains("then resume this request", waiting.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderCapacityWait_PreservesFifoWithinTheSameQueueLane()
    {
        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "openhands-worker-concurrency-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var databasePath = Path.Combine(testRoot, "queue.db");
        var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<AppDbContext>(builder => builder.UseSqlite("Data Source=" + databasePath))
            .BuildServiceProvider();
        var gate = new ProviderConcurrencyGate();
        IDisposable? occupiedProviderLease = null;
        Guid firstRequestId;
        Guid secondRequestId;
        try
        {
            await using (var setupScope = services.CreateAsyncScope())
            {
                var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();
                var machine = new TargetMachine
                {
                    Name = "FIFO test machine",
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                    WorkingRoot = testRoot,
                };
                var project = new Project
                {
                    Name = "FIFO test project",
                    Path = testRoot,
                    Machine = machine,
                };
                var profile = new AiProviderProfile
                {
                    Name = "FIFO test Ollama",
                    Source = AiProviderSource.Local,
                    BaseUrl = "http://ollama.test:11434/v1",
                    MaximumConcurrency = 1,
                    Enabled = true,
                };
                var first = CreateRequest(ExecutionRunner.OpenHandsCli, QueueStatus.Queued, 1);
                first.Project = project;
                first.Machine = machine;
                first.ProviderProfile = profile;
                first.ProviderProfileId = profile.Id;
                first.ExecutionProjectPath = project.Path;
                first.ExecutionMachineUpdatedAt = machine.UpdatedAt;
                first.Model = "openai/test-model";
                first.PermissionMode = PermissionMode.FullAccess;
                first.OpenHandsAlwaysApproveConfirmed = true;
                foreach (var run in first.Runs)
                {
                    run.ProviderProfileId = profile.Id;
                    run.ProviderProfileName = profile.Name;
                    run.ProviderSource = profile.Source;
                    run.Model = first.Model;
                }

                var second = CreateRequest(ExecutionRunner.CodexCli, QueueStatus.Queued, 2);
                second.Project = project;
                second.Machine = machine;
                second.Model = "gpt-5";
                foreach (var run in second.Runs)
                {
                    run.Model = second.Model;
                }

                firstRequestId = first.Id;
                secondRequestId = second.Id;
                db.Requests.AddRange(first, second);
                await db.SaveChangesAsync();

                Assert.True(gate.TryAcquire(
                    QueueWorker.ProviderConcurrencyKey(profile),
                    profile.MaximumConcurrency,
                    out occupiedProviderLease));
            }

            var worker = new QueueWorker(
                services.GetRequiredService<IServiceScopeFactory>(),
                new TargetCommandRunner(NullLogger<TargetCommandRunner>.Instance),
                new FailIfDispatchedResolver(),
                gate,
                NullLogger<QueueWorker>.Instance);
            try
            {
                Assert.True(await worker.KickQueueAsync(CancellationToken.None));
                await WaitUntilAsync(async () =>
                {
                    await using var scope = services.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var waitReason = await db.Requests
                        .Where(request => request.Id == firstRequestId)
                        .Select(request => request.QueueWaitReason)
                        .SingleAsync();
                    return waitReason is not null && !worker.GetDiagnostics().IsProcessing;
                }, TimeSpan.FromSeconds(5));

                await using var verificationScope = services.CreateAsyncScope();
                var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var statuses = await verificationDb.Requests
                    .Where(request => request.Id == firstRequestId || request.Id == secondRequestId)
                    .ToDictionaryAsync(request => request.Id, request => request.Status);
                Assert.Equal(QueueStatus.Queued, statuses[firstRequestId]);
                Assert.Equal(QueueStatus.Queued, statuses[secondRequestId]);
            }
            finally
            {
                worker.Dispose();
            }
        }
        finally
        {
            occupiedProviderLease?.Dispose();
            await services.DisposeAsync();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static CodexRequest CreateRequest(
        ExecutionRunner executionRunner,
        QueueStatus status,
        int queueOrder)
    {
        var request = new CodexRequest
        {
            Prompt = "test request",
            Model = executionRunner == ExecutionRunner.OpenHandsCli
                ? "openai/test-model"
                : "gpt-5",
            ExecutionRunner = executionRunner,
            QueueOrder = queueOrder,
            Status = status,
            StartedAt = status == QueueStatus.Running ? DateTimeOffset.UtcNow : null,
        };
        request.Runs.Add(new CodexRun
        {
            Request = request,
            Kind = RunKind.Request,
            Model = request.Model,
            ExecutionRunner = executionRunner,
            Status = status,
            StartedAt = request.StartedAt,
        });
        return request;
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

    private sealed class FailIfDispatchedResolver : IQueueAgentRunnerResolver
    {
        public IQueueAgentRunner Resolve(ExecutionRunner executionRunner) =>
            throw new InvalidOperationException(
                "A later request in the provider-blocked queue lane must not dispatch.");
    }
}
