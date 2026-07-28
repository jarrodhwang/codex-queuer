using System.Text.Json;
using System.Text.Json.Serialization;
using CodexQueue.Api.Data;
using CodexQueue.Api.Domain;
using CodexQueue.Api.Endpoints;
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
        Assert.Equal(LocalAiServerType.Ollama, new AiProviderProfile().LocalAiServerType);
        Assert.False(new Project().DefaultInternetSearchEnabled);
        Assert.False(new CodexRequest().InternetSearchEnabled);
    }

    [Fact]
    public void LegacyProviderPayload_DefaultsLocalAiServerTypeToOllama()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var input = JsonSerializer.Deserialize<SaveAiProviderProfileRequest>(
            """
            {
              "name": "Legacy Local AI",
              "source": "Local",
              "baseUrl": "http://local-ai.test:11434/v1",
              "modelDiscoveryMode": "Auto",
              "enabled": true,
              "maximumConcurrency": 1
            }
            """,
            options);

        Assert.NotNull(input);
        Assert.Equal(LocalAiServerType.Ollama, input.LocalAiServerType);
    }

    [Theory]
    [InlineData(LocalAiServerType.LmStudio)]
    [InlineData(LocalAiServerType.LlamaCpp)]
    public async Task LocalAiServerType_RoundTripsThroughPersistence(
        LocalAiServerType serverType)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var profileId = Guid.NewGuid();

        await using (var writeDb = new AppDbContext(options))
        {
            await writeDb.Database.EnsureCreatedAsync();
            writeDb.AiProviderProfiles.Add(new AiProviderProfile
            {
                Id = profileId,
                Name = "Persisted " + serverType,
                Source = AiProviderSource.Local,
                LocalAiServerType = serverType,
                BaseUrl = "http://local-ai.test:8080/v1",
                ModelDiscoveryMode = ModelDiscoveryMode.OpenAi,
                MaximumConcurrency = 1,
            });
            await writeDb.SaveChangesAsync();
        }

        await using var readDb = new AppDbContext(options);
        var persisted = await readDb.AiProviderProfiles
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);

        Assert.Equal(serverType, persisted.LocalAiServerType);
    }

    [Fact]
    public async Task DbInitializer_AddsOllamaServerTypeForLegacyLocalProfiles()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var profileId = Guid.NewGuid();

        await using (var setupDb = new AppDbContext(options))
        {
            await setupDb.Database.EnsureCreatedAsync();
            setupDb.AiProviderProfiles.Add(new AiProviderProfile
            {
                Id = profileId,
                Name = "Legacy Local AI",
                Source = AiProviderSource.Local,
                LocalAiServerType = LocalAiServerType.LlamaCpp,
                BaseUrl = "http://local-ai.test:8080/v1",
                ModelDiscoveryMode = ModelDiscoveryMode.OpenAi,
                MaximumConcurrency = 1,
            });
            await setupDb.SaveChangesAsync();
            await setupDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"AiProviderProfiles\" DROP COLUMN \"LocalAiServerType\"");
            await setupDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"QueueTabs\" DROP COLUMN \"LocalCodexSessionId\"");
            await setupDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"QueueTabs\" DROP COLUMN \"LocalCodexSessionRouteKey\"");
            await setupDb.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Runs\" DROP COLUMN \"LocalCodexSessionId\"");
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
            var profile = await db.AiProviderProfiles.SingleAsync(x => x.Id == profileId);
            Assert.Equal(LocalAiServerType.Ollama, profile.LocalAiServerType);
            Assert.Equal(ModelDiscoveryMode.OpenAi, profile.ModelDiscoveryMode);
            Assert.Contains(
                "LocalCodexSessionId",
                await ReadColumnNamesAsync(connection, "QueueTabs"));
            Assert.Contains(
                "LocalCodexSessionRouteKey",
                await ReadColumnNamesAsync(connection, "QueueTabs"));
            Assert.Contains(
                "LocalCodexSessionId",
                await ReadColumnNamesAsync(connection, "Runs"));
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DbInitializer_InsertsMissingSeparateCommitRunForCompletedMainStage(
        bool mainUsesLocalCodex)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var requestId = Guid.NewGuid();
        var commitProviderId = Guid.NewGuid();

        await using (var setupDb = new AppDbContext(options))
        {
            await setupDb.Database.EnsureCreatedAsync();
            var machine = new TargetMachine
            {
                Name = "Separate commit recovery machine",
                Kind = MachineKind.Local,
                Platform = MachinePlatform.Linux,
                WorkingRoot = "/work",
            };
            var project = new Project
            {
                Name = "Separate commit recovery project",
                Path = "/work/project",
                Machine = machine,
            };
            var commitProvider = new AiProviderProfile
            {
                Id = commitProviderId,
                Name = "Recovery Ollama",
                Source = AiProviderSource.Local,
                BaseUrl = "http://ollama.test:11434/v1",
                Enabled = true,
            };
            var request = new CodexRequest
            {
                Id = requestId,
                Project = project,
                Machine = machine,
                Prompt = "completed main stage",
                Model = mainUsesLocalCodex ? "local-main" : "gpt-main",
                ExecutionRunner = mainUsesLocalCodex
                    ? ExecutionRunner.OpenHandsCli
                    : ExecutionRunner.CodexCli,
                ProviderProfileId = mainUsesLocalCodex
                    ? commitProviderId
                    : null,
                ProviderProfile = mainUsesLocalCodex
                    ? commitProvider
                    : null,
                OpenHandsAlwaysApproveConfirmed = mainUsesLocalCodex,
                GenerateCommit = true,
                SeparateCommitSession = true,
                CommitExecutionRunner = ExecutionRunner.OpenHandsCli,
                CommitProviderProfileId = commitProviderId,
                CommitModel = "local-commit",
                Status = QueueStatus.Running,
            };
            setupDb.AiProviderProfiles.Add(commitProvider);
            setupDb.Runs.Add(new CodexRun
            {
                Request = request,
                Kind = RunKind.Request,
                Model = request.Model,
                ExecutionRunner = request.ExecutionRunner,
                Status = QueueStatus.Succeeded,
                FinishedAt = DateTimeOffset.UtcNow,
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
            var request = await db.Requests
                .Include(item => item.Runs)
                .SingleAsync(item => item.Id == requestId);
            var commitRun = Assert.Single(
                request.Runs,
                run => run.Kind == RunKind.Commit);
            Assert.Equal(QueueStatus.Queued, request.Status);
            Assert.Equal(QueueStatus.Queued, commitRun.Status);
            Assert.Equal(ExecutionRunner.OpenHandsCli, commitRun.ExecutionRunner);
            Assert.Equal(commitProviderId, commitRun.ProviderProfileId);
            Assert.Equal("local-commit", commitRun.Model);
        }
    }

    [Fact]
    public async Task DbInitializer_DoesNotAutomaticallyDuplicateInterruptedLocalCodexRun()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var requestId = Guid.NewGuid();
        var queuedLocalCodexRequestId = Guid.NewGuid();
        var queuedCodexRequestId = Guid.NewGuid();

        await using (var setupDb = new AppDbContext(options))
        {
            await setupDb.Database.EnsureCreatedAsync();
            var machine = new TargetMachine
            {
                Name = "Local Codex machine",
                Kind = MachineKind.Local,
                Platform = MachinePlatform.Linux,
                WorkingRoot = "/work",
            };
            var project = new Project
            {
                Name = "Local Codex project",
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
            var queuedLocalCodexRequest = new CodexRequest
            {
                Id = queuedLocalCodexRequestId,
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
                Request = queuedLocalCodexRequest,
                Kind = RunKind.Request,
                Model = queuedLocalCodexRequest.Model,
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
            Assert.Contains("orphaned Codex process", request.Error, StringComparison.Ordinal);
            Assert.All(request.Runs, run => Assert.Equal(QueueStatus.Failed, run.Status));

            var queuedLocalCodexRequest = await db.Requests
                .Include(x => x.Runs)
                .SingleAsync(x => x.Id == queuedLocalCodexRequestId);
            Assert.Equal(QueueStatus.Failed, queuedLocalCodexRequest.Status);
            Assert.Contains("then resume this request", queuedLocalCodexRequest.Error, StringComparison.Ordinal);
            Assert.All(queuedLocalCodexRequest.Runs, run => Assert.Equal(QueueStatus.Failed, run.Status));

            var queuedCodexRequest = await db.Requests
                .Include(x => x.Runs)
                .SingleAsync(x => x.Id == queuedCodexRequestId);
            Assert.Equal(QueueStatus.Queued, queuedCodexRequest.Status);
            Assert.All(queuedCodexRequest.Runs, run => Assert.Equal(QueueStatus.Queued, run.Status));
        }
    }

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = tableName switch
        {
            "QueueTabs" => "PRAGMA table_info(\"QueueTabs\")",
            "Runs" => "PRAGMA table_info(\"Runs\")",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName)),
        };
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return names;
    }
}
