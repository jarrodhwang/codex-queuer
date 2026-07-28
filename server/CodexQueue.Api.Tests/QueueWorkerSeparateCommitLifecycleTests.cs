using System.Diagnostics;
using CodexQueue.Api.Data;
using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexQueue.Api.Tests;

public sealed class QueueWorkerSeparateCommitLifecycleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SeparateCommitSession_ChainsOrRecoversCommitStageAndSwitchesRunner(
        bool startsAfterCompletedMainStage,
        bool mainUsesLocalCodex)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "separate-commit-lifecycle-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        Directory.CreateDirectory(projectRoot);
        var databasePath = Path.Combine(testRoot, "queue.db");
        var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<AppDbContext>(
                builder => builder.UseSqlite("Data Source=" + databasePath))
            .BuildServiceProvider();
        var providerGate = new ProviderConcurrencyGate();

        try
        {
            await RunGitAsync(projectRoot, "init");
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "baseline.txt"),
                "baseline");
            await RunGitAsync(projectRoot, "add", "-A", "--", ".");
            await RunGitAsync(
                projectRoot,
                "-c",
                "user.name=Codex Queue Tests",
                "-c",
                "user.email=codex-queue-tests@example.invalid",
                "commit",
                "-m",
                "Initial commit");
            var initialHead = (await RunGitAsync(
                projectRoot,
                "rev-parse",
                "HEAD")).Trim();
            if (startsAfterCompletedMainStage)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(projectRoot, "requested.txt"),
                    "created before commit recovery");
            }

            Guid requestId;
            Guid providerProfileId;
            await using (var setupScope = services.CreateAsyncScope())
            {
                var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();
                var machine = new TargetMachine
                {
                    Name = "Separate commit test machine",
                    Kind = MachineKind.Local,
                    Platform = MachinePlatform.Linux,
                    WorkingRoot = testRoot,
                };
                var project = new Project
                {
                    Name = "Separate commit test project",
                    Path = projectRoot,
                    Machine = machine,
                };
                var commitProvider = new AiProviderProfile
                {
                    Name = "Commit Ollama",
                    Source = AiProviderSource.Local,
                    BaseUrl = "http://ollama.test:11434/v1",
                    MaximumConcurrency = 1,
                    Enabled = true,
                };
                var request = new CodexRequest
                {
                    Project = project,
                    Machine = machine,
                    ExecutionRunner = mainUsesLocalCodex
                        ? ExecutionRunner.OpenHandsCli
                        : ExecutionRunner.CodexCli,
                    ProviderProfileId = mainUsesLocalCodex
                        ? commitProvider.Id
                        : null,
                    ProviderProfile = mainUsesLocalCodex
                        ? commitProvider
                        : null,
                    OpenHandsAlwaysApproveConfirmed = mainUsesLocalCodex,
                    ExecutionProjectPath = mainUsesLocalCodex
                        ? projectRoot
                        : null,
                    ExecutionMachineUpdatedAt = mainUsesLocalCodex
                        ? machine.UpdatedAt
                        : null,
                    Prompt = "Create the requested file.",
                    Model = mainUsesLocalCodex
                        ? "local-test-main"
                        : "gpt-test-main",
                    GenerateCommit = true,
                    SeparateCommitSession = true,
                    PermissionMode = PermissionMode.FullAccess,
                    CommitExecutionRunner = ExecutionRunner.OpenHandsCli,
                    CommitProviderProfileId = commitProvider.Id,
                    CommitModel = "local-test-commit",
                    QueueOrder = 1,
                    Status = startsAfterCompletedMainStage
                        ? QueueStatus.Running
                        : QueueStatus.Queued,
                };
                request.Runs.Add(new CodexRun
                {
                    Kind = RunKind.Request,
                    Model = request.Model,
                    ExecutionRunner = request.ExecutionRunner,
                    ProviderProfileId = request.ProviderProfileId,
                    Status = startsAfterCompletedMainStage
                        ? QueueStatus.Succeeded
                        : QueueStatus.Queued,
                    StartedAt = startsAfterCompletedMainStage
                        ? DateTimeOffset.UtcNow.AddMinutes(-1)
                        : null,
                    FinishedAt = startsAfterCompletedMainStage
                        ? DateTimeOffset.UtcNow
                        : null,
                });
                db.AiProviderProfiles.Add(commitProvider);
                db.Requests.Add(request);
                await db.SaveChangesAsync();
                requestId = request.Id;
                providerProfileId = commitProvider.Id;
            }

            var resolver = new SeparateCommitRunnerResolver();
            var worker = new QueueWorker(
                services.GetRequiredService<IServiceScopeFactory>(),
                new TargetCommandRunner(NullLogger<TargetCommandRunner>.Instance),
                resolver,
                providerGate,
                NullLogger<QueueWorker>.Instance);
            try
            {
                Assert.True(await worker.KickQueueAsync(CancellationToken.None));
                await WaitUntilAsync(async () =>
                {
                    await using var scope = services.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    return await db.Requests
                            .Where(request => request.Id == requestId)
                            .Select(request => request.Status)
                            .SingleAsync()
                        == QueueStatus.Succeeded
                        && worker.GetDiagnostics().ActiveRequestIds.Count == 0;
                }, TimeSpan.FromSeconds(10));

                await using var verificationScope = services.CreateAsyncScope();
                var verificationDb =
                    verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var persisted = await verificationDb.Requests
                    .Include(request => request.Runs)
                    .SingleAsync(request => request.Id == requestId);
                var requestRun = Assert.Single(
                    persisted.Runs,
                    run => run.Kind == RunKind.Request);
                var commitRun = Assert.Single(
                    persisted.Runs,
                    run => run.Kind == RunKind.Commit);

                Assert.Equal(QueueStatus.Succeeded, requestRun.Status);
                Assert.Equal(QueueStatus.Succeeded, commitRun.Status);
                Assert.Equal(ExecutionRunner.OpenHandsCli, commitRun.ExecutionRunner);
                Assert.Equal(providerProfileId, commitRun.ProviderProfileId);
                Assert.Equal("local-commit-session", commitRun.LocalCodexSessionId);
                Assert.Null(commitRun.CodexSessionId);
                Assert.Contains(
                    startsAfterCompletedMainStage
                        ? "Dispatching Local Codex commit run"
                        : "Starting separate Local Codex commit run",
                    commitRun.Output);
                Assert.Equal(
                    startsAfterCompletedMainStage
                        ? [ExecutionRunner.OpenHandsCli]
                        : mainUsesLocalCodex
                            ? [ExecutionRunner.OpenHandsCli, ExecutionRunner.OpenHandsCli]
                            : [ExecutionRunner.CodexCli, ExecutionRunner.OpenHandsCli],
                    resolver.ExecutionOrder);
                Assert.Equal(0, providerGate.ActiveCount(
                    QueueWorker.ProviderConcurrencyKey(
                        await verificationDb.AiProviderProfiles.SingleAsync())));

                var committedHead = (await RunGitAsync(
                    projectRoot,
                    "rev-parse",
                    "HEAD")).Trim();
                Assert.NotEqual(initialHead, committedHead);
                Assert.Equal(
                    "separate commit lifecycle",
                    (await RunGitAsync(
                        projectRoot,
                        "log",
                        "-1",
                        "--pretty=%s")).Trim());
                Assert.Empty((await RunGitAsync(
                    projectRoot,
                    "status",
                    "--porcelain",
                    "--",
                    ".")).Trim());
            }
            finally
            {
                worker.Dispose();
            }
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

    private static async Task WaitUntilAsync(
        Func<Task<bool>> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!await predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "Separate commit lifecycle did not finish before the timeout.");
            }

            await Task.Delay(25);
        }
    }

    private static async Task<string> RunGitAsync(
        string workingDirectory,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(
            process.ExitCode == 0,
            "git " + string.Join(" ", arguments) + " failed: " + error);
        return output;
    }

    private sealed class SeparateCommitRunnerResolver : IQueueAgentRunnerResolver
    {
        private readonly object _sync = new();
        private readonly List<ExecutionRunner> _executionOrder = [];
        private readonly IQueueAgentRunner _cloudRunner;
        private readonly IQueueAgentRunner _localRunner;

        public SeparateCommitRunnerResolver()
        {
            _cloudRunner = new TestRunner(
                ExecutionRunner.CodexCli,
                async context =>
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(context.ProjectPath, "requested.txt"),
                        "created by main request");
                    return new QueueAgentRunResult(
                        0,
                        "Main request completed.",
                        "codex exec <main>");
                });
            _localRunner = new TestRunner(
                ExecutionRunner.OpenHandsCli,
                async context =>
                {
                    if (context.Run.Kind == RunKind.Request)
                    {
                        await File.WriteAllTextAsync(
                            Path.Combine(context.ProjectPath, "requested.txt"),
                            "created by Local Codex main request");
                        return new QueueAgentRunResult(
                            0,
                            "Local Codex main request completed.",
                            "codex exec <local-main>",
                            LocalCodexSessionId: "local-main-session");
                    }

                    await RunGitAsync(context.ProjectPath, "add", "-A", "--", ".");
                    var output = await RunGitAsync(
                        context.ProjectPath,
                        "-c",
                        "user.name=Codex Queue Tests",
                        "-c",
                        "user.email=codex-queue-tests@example.invalid",
                        "commit",
                        "-m",
                        "separate commit lifecycle");
                    return new QueueAgentRunResult(
                        0,
                        output,
                        "codex exec <commit>",
                        LocalCodexSessionId: "local-commit-session");
                });
        }

        public IReadOnlyList<ExecutionRunner> ExecutionOrder
        {
            get
            {
                lock (_sync)
                {
                    return _executionOrder.ToArray();
                }
            }
        }

        public IQueueAgentRunner Resolve(ExecutionRunner executionRunner)
        {
            lock (_sync)
            {
                _executionOrder.Add(executionRunner);
            }

            return executionRunner == ExecutionRunner.CodexCli
                ? _cloudRunner
                : _localRunner;
        }
    }

    private sealed class TestRunner(
        ExecutionRunner executionRunner,
        Func<QueueAgentRunContext, Task<QueueAgentRunResult>> execute)
        : IQueueAgentRunner
    {
        public ExecutionRunner ExecutionRunner { get; } = executionRunner;

        public Task<QueueAgentRunResult> RunAsync(
            QueueAgentRunContext context,
            Func<string, Task> onOutput,
            CancellationToken cancellationToken) =>
            execute(context);
    }
}
