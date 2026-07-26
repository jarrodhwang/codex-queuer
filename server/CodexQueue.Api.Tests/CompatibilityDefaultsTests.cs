using CodexQueue.Api.Data;
using CodexQueue.Api.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodexQueue.Api.Tests;

public sealed class CompatibilityDefaultsTests
{
    [Fact]
    public void ExecutionRunnerAndEntities_DefaultToCodexCli()
    {
        Assert.Equal(ExecutionRunner.CodexCli, default(ExecutionRunner));
        Assert.Equal(ExecutionRunner.CodexCli, new Project().DefaultExecutionRunner);
        Assert.Equal(ExecutionRunner.CodexCli, new CodexRequest().ExecutionRunner);
        Assert.Equal(ExecutionRunner.CodexCli, new CodexRun().ExecutionRunner);
    }

    [Fact]
    public async Task DbInitializer_AddsRunnerColumnsWithCodexDefaultsForLegacyRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        await using (var setupDb = new AppDbContext(options))
        {
            await setupDb.Database.EnsureCreatedAsync();
            var machine = new TargetMachine
            {
                Name = "Legacy machine",
                Kind = MachineKind.Local,
                Platform = MachinePlatform.Linux,
                WorkingRoot = "/legacy",
            };
            var project = new Project
            {
                Id = projectId,
                Name = "Legacy project",
                Path = "/legacy/project",
                Machine = machine,
                DefaultModel = "gpt-5",
                DefaultModelEffort = "high",
            };
            var request = new CodexRequest
            {
                Id = requestId,
                Project = project,
                Machine = machine,
                Prompt = "legacy request",
                Model = "gpt-5",
                Status = QueueStatus.Succeeded,
            };
            setupDb.Runs.Add(new CodexRun
            {
                Id = runId,
                Request = request,
                Kind = RunKind.Request,
                Model = "gpt-5",
                Status = QueueStatus.Succeeded,
                Output = "done",
            });
            await setupDb.SaveChangesAsync();

            await setupDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Requests\" DROP COLUMN \"ExecutionRunner\"");
            await setupDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Runs\" DROP COLUMN \"ExecutionRunner\"");
            await setupDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Projects\" DROP COLUMN \"DefaultExecutionRunner\"");
            await setupDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Projects\" DROP COLUMN \"DefaultLocalModel\"");
            await setupDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Projects\" DROP COLUMN \"DefaultLocalModelEffort\"");
            await setupDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Projects\" DROP COLUMN \"DefaultLocalModelSpeed\"");
        }

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection().Build())
            .AddDbContext<AppDbContext>(builder => builder.UseSqlite(connection))
            .BuildServiceProvider();
        await using (services)
        {
            await DbInitializer.InitializeAsync(services);

            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var request = await db.Requests.SingleAsync(x => x.Id == requestId);
            var run = await db.Runs.SingleAsync(x => x.Id == runId);
            var project = await db.Projects.SingleAsync(x => x.Id == projectId);
            Assert.Equal(ExecutionRunner.CodexCli, request.ExecutionRunner);
            Assert.Equal(ExecutionRunner.CodexCli, run.ExecutionRunner);
            Assert.Equal(ExecutionRunner.CodexCli, project.DefaultExecutionRunner);
            Assert.Equal("gpt-5", project.DefaultModel);
            Assert.Equal("high", project.DefaultModelEffort);
            Assert.Null(project.DefaultLocalModel);
            Assert.Null(project.DefaultLocalModelEffort);
            Assert.Null(project.DefaultLocalModelSpeed);
        }
    }

    [Fact]
    public async Task DbInitializer_DoesNotAutomaticallyDuplicateInterruptedOpenHandsRun()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var requestId = Guid.NewGuid();
        var queuedOpenHandsRequestId = Guid.NewGuid();
        var queuedCodexRequestId = Guid.NewGuid();

        await using (var setupDb = new AppDbContext(options))
        {
            await setupDb.Database.EnsureCreatedAsync();
            var machine = new TargetMachine
            {
                Name = "OpenHands machine",
                Kind = MachineKind.Local,
                Platform = MachinePlatform.Linux,
                WorkingRoot = "/work",
            };
            var project = new Project
            {
                Name = "OpenHands project",
                Path = "/work/project",
                Machine = machine,
            };
            var request = new CodexRequest
            {
                Id = requestId,
                Project = project,
                Machine = machine,
                Prompt = "interrupted task",
                Model = "openai/local-model",
                ExecutionRunner = ExecutionRunner.OpenHandsCli,
                PermissionMode = PermissionMode.FullAccess,
                OpenHandsAlwaysApproveConfirmed = true,
                Status = QueueStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
            };
            setupDb.Runs.Add(new CodexRun
            {
                Request = request,
                Kind = RunKind.Request,
                Model = request.Model,
                ExecutionRunner = ExecutionRunner.OpenHandsCli,
                Status = QueueStatus.Running,
                StartedAt = request.StartedAt,
            });
            var queuedOpenHandsRequest = new CodexRequest
            {
                Id = queuedOpenHandsRequestId,
                Project = project,
                Machine = machine,
                Prompt = "waiting local task",
                Model = "openai/local-model",
                ExecutionRunner = ExecutionRunner.OpenHandsCli,
                PermissionMode = PermissionMode.FullAccess,
                OpenHandsAlwaysApproveConfirmed = true,
                Status = QueueStatus.Queued,
            };
            setupDb.Runs.Add(new CodexRun
            {
                Request = queuedOpenHandsRequest,
                Kind = RunKind.Request,
                Model = queuedOpenHandsRequest.Model,
                ExecutionRunner = ExecutionRunner.OpenHandsCli,
                Status = QueueStatus.Queued,
            });
            var queuedCodexRequest = new CodexRequest
            {
                Id = queuedCodexRequestId,
                Project = project,
                Machine = machine,
                Prompt = "waiting Codex task",
                Model = "gpt-5",
                ExecutionRunner = ExecutionRunner.CodexCli,
                Status = QueueStatus.Queued,
            };
            setupDb.Runs.Add(new CodexRun
            {
                Request = queuedCodexRequest,
                Kind = RunKind.Request,
                Model = queuedCodexRequest.Model,
                ExecutionRunner = ExecutionRunner.CodexCli,
                Status = QueueStatus.Queued,
            });
            await setupDb.SaveChangesAsync();
        }

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection().Build())
            .AddDbContext<AppDbContext>(builder => builder.UseSqlite(connection))
            .BuildServiceProvider();
        await using (services)
        {
            await DbInitializer.InitializeAsync(services);

            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var request = await db.Requests.Include(x => x.Runs).SingleAsync(x => x.Id == requestId);
            Assert.Equal(QueueStatus.Failed, request.Status);
            Assert.Contains("orphaned OpenHands process", request.Error, StringComparison.Ordinal);
            Assert.All(request.Runs, run => Assert.Equal(QueueStatus.Failed, run.Status));

            var queuedOpenHandsRequest = await db.Requests
                .Include(x => x.Runs)
                .SingleAsync(x => x.Id == queuedOpenHandsRequestId);
            Assert.Equal(QueueStatus.Failed, queuedOpenHandsRequest.Status);
            Assert.Contains("then resume this request", queuedOpenHandsRequest.Error, StringComparison.Ordinal);
            Assert.All(queuedOpenHandsRequest.Runs, run => Assert.Equal(QueueStatus.Failed, run.Status));

            var queuedCodexRequest = await db.Requests
                .Include(x => x.Runs)
                .SingleAsync(x => x.Id == queuedCodexRequestId);
            Assert.Equal(QueueStatus.Queued, queuedCodexRequest.Status);
            Assert.All(queuedCodexRequest.Runs, run => Assert.Equal(QueueStatus.Queued, run.Status));
        }
    }
}
