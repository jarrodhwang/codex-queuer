using CodexQueue.Api.Data;
using CodexQueue.Api.Domain;
using CodexQueue.Api.Endpoints;
using CodexQueue.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CodexQueue.Api.Tests;

public sealed class LocalProjectDefaultsTests
{
    [Fact]
    public async Task NormalizeLocalProjectDefaults_PreservesRawModelAndNormalizesEffortWithoutDiscovery()
    {
        await using var fixture = await LocalDefaultsFixture.CreateAsync();
        var profile = new AiProviderProfile
        {
            Name = "Ollama",
            Source = AiProviderSource.Local,
            BaseUrl = "http://localhost:11434",
            ConfiguredContextWindow = AiProviderService.ContextWarningThreshold,
        };
        fixture.Db.AiProviderProfiles.Add(profile);
        await fixture.Db.SaveChangesAsync();

        var result = await ApiEndpoints.NormalizeLocalProjectDefaultsAsync(
            CreateRequest(
                ExecutionRunner.OpenHandsCli,
                profile.Id,
                "gpt-oss:20b",
                "HIGH",
                "32768"),
            fixture.Db,
            fixture.Providers,
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(profile.Id, result.ProviderProfileId);
        Assert.Equal("gpt-oss:20b", result.Model);
        Assert.Equal("high", result.Effort);
        Assert.Equal("32768", result.ContextWindow);
    }

    [Fact]
    public async Task NormalizeLocalProjectDefaults_RejectsInvalidEffort()
    {
        await using var fixture = await LocalDefaultsFixture.CreateAsync();
        var profile = new AiProviderProfile
        {
            Name = "Ollama",
            Source = AiProviderSource.Local,
            BaseUrl = "http://localhost:11434",
            ConfiguredContextWindow = AiProviderService.ContextWarningThreshold,
        };
        fixture.Db.AiProviderProfiles.Add(profile);
        await fixture.Db.SaveChangesAsync();

        var result = await ApiEndpoints.NormalizeLocalProjectDefaultsAsync(
            CreateRequest(ExecutionRunner.OpenHandsCli, profile.Id, "qwen3", "ultra"),
            fixture.Db,
            fixture.Providers,
            CancellationToken.None);

        Assert.Equal("Local reasoning effort must be low, medium, or high.", result.Error);
    }

    [Fact]
    public async Task NormalizeLocalProjectDefaults_RejectsContextSmallerThanLocalCodexMinimum()
    {
        await using var fixture = await LocalDefaultsFixture.CreateAsync();
        var profile = new AiProviderProfile
        {
            Name = "Ollama",
            Source = AiProviderSource.Local,
            BaseUrl = "http://localhost:11434",
            ConfiguredContextWindow = AiProviderService.RecommendedContextWindow,
        };
        fixture.Db.AiProviderProfiles.Add(profile);
        await fixture.Db.SaveChangesAsync();

        var result = await ApiEndpoints.NormalizeLocalProjectDefaultsAsync(
            CreateRequest(ExecutionRunner.OpenHandsCli, profile.Id, "gpt-oss:20b", "low", "16384"),
            fixture.Db,
            fixture.Providers,
            CancellationToken.None);

        Assert.Contains("at least", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AiProviderService.ContextWarningThreshold.ToString("N0"), result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalizeLocalProjectDefaults_RejectsContextAboveCurrentCodexFallbackLimit()
    {
        await using var fixture = await LocalDefaultsFixture.CreateAsync();
        var profile = new AiProviderProfile
        {
            Name = "Large-context Local AI",
            Source = AiProviderSource.Local,
            BaseUrl = "http://localhost:11434",
            ConfiguredContextWindow = 1_048_576,
        };
        fixture.Db.AiProviderProfiles.Add(profile);
        await fixture.Db.SaveChangesAsync();

        var result = await ApiEndpoints.NormalizeLocalProjectDefaultsAsync(
            CreateRequest(
                ExecutionRunner.OpenHandsCli,
                profile.Id,
                "gpt-oss:20b",
                "low",
                "524288"),
            fixture.Db,
            fixture.Providers,
            CancellationToken.None);

        Assert.Contains("between 4K and 256K", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalizeLocalProjectDefaults_RequiresProfileAndModelForLocalRunner()
    {
        await using var fixture = await LocalDefaultsFixture.CreateAsync();

        var noProfile = await ApiEndpoints.NormalizeLocalProjectDefaultsAsync(
            CreateRequest(ExecutionRunner.OpenHandsCli, null, "gpt-oss:20b", "low"),
            fixture.Db,
            fixture.Providers,
            CancellationToken.None);
        Assert.Equal("A Local AI Server profile is required when Local is the default runner.", noProfile.Error);

        var profile = new AiProviderProfile
        {
            Name = "Ollama",
            Source = AiProviderSource.Local,
            BaseUrl = "http://localhost:11434",
            ConfiguredContextWindow = AiProviderService.ContextWarningThreshold,
        };
        fixture.Db.AiProviderProfiles.Add(profile);
        await fixture.Db.SaveChangesAsync();
        var noModel = await ApiEndpoints.NormalizeLocalProjectDefaultsAsync(
            CreateRequest(ExecutionRunner.OpenHandsCli, profile.Id, null, null),
            fixture.Db,
            fixture.Providers,
            CancellationToken.None);
        Assert.Equal("A Local model is required when Local is the default runner.", noModel.Error);
    }

    [Fact]
    public async Task NormalizeLocalProjectDefaults_RejectsDisabledAndUndersizedProfiles()
    {
        await using var fixture = await LocalDefaultsFixture.CreateAsync();
        var disabled = new AiProviderProfile
        {
            Name = "Disabled Ollama",
            Source = AiProviderSource.Local,
            BaseUrl = "http://localhost:11434",
            Enabled = false,
            ConfiguredContextWindow = AiProviderService.ContextWarningThreshold,
        };
        var undersized = new AiProviderProfile
        {
            Name = "Small-context Ollama",
            Source = AiProviderSource.Local,
            BaseUrl = "http://localhost:11435",
            ConfiguredContextWindow = AiProviderService.ContextWarningThreshold - 1,
        };
        fixture.Db.AiProviderProfiles.AddRange(disabled, undersized);
        await fixture.Db.SaveChangesAsync();

        var disabledResult = await ApiEndpoints.NormalizeLocalProjectDefaultsAsync(
            CreateRequest(ExecutionRunner.OpenHandsCli, disabled.Id, "qwen3", "medium"),
            fixture.Db,
            fixture.Providers,
            CancellationToken.None);
        var undersizedResult = await ApiEndpoints.NormalizeLocalProjectDefaultsAsync(
            CreateRequest(ExecutionRunner.OpenHandsCli, undersized.Id, "qwen3", "medium"),
            fixture.Db,
            fixture.Providers,
            CancellationToken.None);

        Assert.Equal("Selected Local AI Server profile is disabled.", disabledResult.Error);
        Assert.Contains(
            AiProviderService.ContextWarningThreshold.ToString("N0"),
            undersizedResult.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalizeLocalProjectDefaults_UsesEffectiveRunnerForPartialUpdate()
    {
        await using var fixture = await LocalDefaultsFixture.CreateAsync();
        var profile = new AiProviderProfile
        {
            Name = "Ollama",
            Source = AiProviderSource.Local,
            BaseUrl = "http://localhost:11434",
            ConfiguredContextWindow = AiProviderService.ContextWarningThreshold,
        };
        fixture.Db.AiProviderProfiles.Add(profile);
        await fixture.Db.SaveChangesAsync();
        var partial = CreateRequest(null, profile.Id, null, null);

        var result = await ApiEndpoints.NormalizeLocalProjectDefaultsAsync(
            partial,
            fixture.Db,
            fixture.Providers,
            CancellationToken.None,
            ExecutionRunner.OpenHandsCli);

        Assert.Equal("A Local model is required when Local is the default runner.", result.Error);
    }

    [Fact]
    public void HasLocalProjectDefaultsInput_DistinguishesLegacyAndCurrentPayloads()
    {
        Assert.False(ApiEndpoints.HasLocalProjectDefaultsInput(
            CreateRequest(null, null, null, null)));
        Assert.True(ApiEndpoints.HasLocalProjectDefaultsInput(
            CreateRequest(ExecutionRunner.CodexCli, null, null, null)));
        Assert.True(ApiEndpoints.HasLocalProjectDefaultsInput(
            CreateRequest(null, null, "gpt-oss:20b", null)));
    }

    [Fact]
    public void MergeLocalProjectDefaultsInput_PreservesOmittedFields()
    {
        var profileId = Guid.NewGuid();
        var project = new Project
        {
            DefaultExecutionRunner = ExecutionRunner.OpenHandsCli,
            DefaultLocalProviderProfileId = profileId,
            DefaultLocalModel = "openai/gpt-oss:20b",
            DefaultLocalModelEffort = "high",
        };
        var effortOnlyUpdate = CreateRequest(null, null, null, "medium");

        var merged = ApiEndpoints.MergeLocalProjectDefaultsInput(
            effortOnlyUpdate,
            project,
            project.DefaultExecutionRunner);

        Assert.Equal(ExecutionRunner.OpenHandsCli, merged.DefaultExecutionRunner);
        Assert.Equal(profileId, merged.DefaultLocalProviderProfileId);
        Assert.Equal("openai/gpt-oss:20b", merged.DefaultLocalModel);
        Assert.Equal("medium", merged.DefaultLocalModelEffort);
    }

    [Fact]
    public void MergeLocalProjectDefaultsInput_ClearsEffortForSuppliedModelWithoutEffort()
    {
        var profileId = Guid.NewGuid();
        var project = new Project
        {
            DefaultExecutionRunner = ExecutionRunner.OpenHandsCli,
            DefaultLocalProviderProfileId = profileId,
            DefaultLocalModel = "openai/gpt-oss:20b",
            DefaultLocalModelEffort = "high",
        };
        var modelUpdate = CreateRequest(
            ExecutionRunner.OpenHandsCli,
            profileId,
            "openai/granite3.3:8b",
            null);

        var merged = ApiEndpoints.MergeLocalProjectDefaultsInput(
            modelUpdate,
            project,
            project.DefaultExecutionRunner);

        Assert.Equal("openai/granite3.3:8b", merged.DefaultLocalModel);
        Assert.Null(merged.DefaultLocalModelEffort);
    }

    private static SaveProjectRequest CreateRequest(
        ExecutionRunner? runner,
        Guid? profileId,
        string? model,
        string? effort,
        string? speed = null) =>
        new(
            Name: "Project",
            Path: "/workspace/project",
            MachineId: Guid.NewGuid(),
            DefaultModel: null,
            DefaultModelEffort: null,
            DefaultModelSpeed: null,
            DefaultCommitModel: null,
            DefaultCommitModelEffort: null,
            DefaultCommitModelSpeed: null,
            DefaultGenerateCommit: null,
            DefaultSeparateCommitSession: null,
            DefaultPermissionMode: null,
            DefaultExecutionRunner: runner,
            DefaultLocalProviderProfileId: profileId,
            DefaultLocalModel: model,
            DefaultLocalModelEffort: effort,
            DefaultLocalModelSpeed: speed,
            SeparateQueuesByTab: null);

    private sealed class LocalDefaultsFixture : IAsyncDisposable
    {
        private LocalDefaultsFixture(
            SqliteConnection connection,
            AppDbContext db,
            IAiProviderService providers)
        {
            Connection = connection;
            Db = db;
            Providers = providers;
        }

        private SqliteConnection Connection { get; }
        public AppDbContext Db { get; }
        public IAiProviderService Providers { get; }

        public static async Task<LocalDefaultsFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();
            return new LocalDefaultsFixture(
                connection,
                db,
                new AiProviderService(new ThrowingHttpClientFactory()));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException(
                "Saving Local defaults must not perform model discovery.");
    }
}
