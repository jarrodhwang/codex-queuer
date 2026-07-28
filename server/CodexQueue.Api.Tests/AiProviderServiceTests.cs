using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexQueue.Api.Domain;
using CodexQueue.Api.Services;

namespace CodexQueue.Api.Tests;

public sealed class AiProviderServiceTests
{
    [Fact]
    public async Task PrepareModelForContextAsync_CreatesOllamaModelWithNumCtx()
    {
        string? requestUri = null;
        string? requestBody = null;
        var service = CreateService(request =>
        {
            requestUri = request.RequestUri?.AbsoluteUri;
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"status":"success"}""");
        });
        var profile = LocalProfile();

        var runtimeModel = await service.PrepareModelForContextAsync(
            profile,
            "gpt-oss:20b",
            131_072);

        Assert.Equal("http://ollama.test:11434/api/create", requestUri);
        Assert.StartsWith("codex-queue-context-", runtimeModel, StringComparison.Ordinal);
        Assert.EndsWith(":ctx-131072", runtimeModel, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        Assert.Equal(runtimeModel, document.RootElement.GetProperty("model").GetString());
        Assert.Equal("gpt-oss:20b", document.RootElement.GetProperty("from").GetString());
        Assert.Equal(
            131_072,
            document.RootElement.GetProperty("parameters").GetProperty("num_ctx").GetInt32());
        Assert.False(document.RootElement.GetProperty("stream").GetBoolean());
    }

    [Theory]
    [InlineData(LocalAiServerType.LmStudio)]
    [InlineData(LocalAiServerType.LlamaCpp)]
    public async Task PrepareModelForContextAsync_LeavesOtherBackendsUnchanged(
        LocalAiServerType serverType)
    {
        var service = CreateService(_ =>
            throw new InvalidOperationException("No backend request was expected."));
        var profile = LocalProfile(serverType: serverType);

        var runtimeModel = await service.PrepareModelForContextAsync(
            profile,
            "local-model",
            65_536);

        Assert.Equal("local-model", runtimeModel);
    }

    [Fact]
    public void Validate_NormalizesLocalRootToOpenAiEndpoint()
    {
        var service = CreateService(_ => JsonResponse("""{"models":[]}"""));
        var profile = LocalProfile(baseUrl: "http://ollama.test:11434/");

        var result = service.Validate(profile);

        Assert.True(result.IsValid);
        Assert.Equal("http://ollama.test:11434/v1", result.NormalizedBaseUrl);
    }

    [Theory]
    [InlineData(
        "http://user:password@ollama.test:11434/v1",
        "must not contain embedded credentials")]
    [InlineData(
        "http://ollama.test:11434/v1?token=secret",
        "must not contain a query string or fragment")]
    [InlineData(
        "http://ollama.test:11434/api",
        "must use the /v1 endpoint")]
    public void Validate_RejectsUnsafeOrUnsupportedLocalUrls(
        string baseUrl,
        string expectedError)
    {
        var service = CreateService(_ => JsonResponse("""{"models":[]}"""));

        var result = service.Validate(LocalProfile(baseUrl));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains(expectedError, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RequiresAValidEnvironmentReferenceForCloudProfiles()
    {
        var service = CreateService(_ => JsonResponse("""{"data":[]}"""));
        var missingReference = CloudProfile(AiProviderSource.OpenAi, null);
        var invalidReference = CloudProfile(AiProviderSource.Anthropic, "ANTHROPIC-KEY");
        var validReference = CloudProfile(AiProviderSource.OpenAi, "OPENAI_API_KEY");

        Assert.Contains(
            service.Validate(missingReference).Errors,
            error => error.Contains("requires an API key", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            service.Validate(invalidReference).Errors,
            error => error.Contains("reference is invalid", StringComparison.OrdinalIgnoreCase));
        Assert.True(service.Validate(validReference).IsValid);
    }

    [Fact]
    public void Validate_RejectsEnvironmentReferencesForLocalProfiles()
    {
        var service = CreateService(_ => JsonResponse("""{"models":[]}"""));
        var profile = LocalProfile();
        profile.ApiKeyEnvironmentVariable = "CQ_API_TOKEN";

        var result = service.Validate(profile);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains(
                "must not configure an API key",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiscoverModelsAsync_DoesNotResolveOrTransmitCloudEnvironmentSecrets()
    {
        const string secretName = "CQ_OPENHANDS_DISCOVERY_SECRET";
        const string secretValue = "must-not-be-transmitted";
        var priorValue = Environment.GetEnvironmentVariable(secretName);
        var requestCount = 0;
        Environment.SetEnvironmentVariable(secretName, secretValue);
        try
        {
            var service = CreateService(_ =>
            {
                Interlocked.Increment(ref requestCount);
                return JsonResponse("""{"data":[]}""");
            });
            var profile = CloudProfile(AiProviderSource.OpenAi, secretName);

            var result = await service.DiscoverModelsAsync(profile, forceRefresh: true);

            Assert.Equal(ProviderHealthStatus.Offline, result.HealthStatus);
            Assert.Contains("disabled", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, requestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, priorValue);
        }
    }

    [Fact]
    public void Validate_RejectsUndefinedProviderEnums()
    {
        var service = CreateService(_ => JsonResponse("""{"models":[]}"""));
        var profile = LocalProfile();
        profile.Source = (AiProviderSource)999;
        profile.LocalAiServerType = (LocalAiServerType)999;
        profile.ModelDiscoveryMode = (ModelDiscoveryMode)999;

        var result = service.Validate(profile);

        Assert.Contains("Provider source is invalid.", result.Errors);
        Assert.Contains("Local AI server type is invalid.", result.Errors);
        Assert.Contains("Model discovery mode is invalid.", result.Errors);
    }

    [Fact]
    public void GetContextWarning_WarnsOnlyBelowLocalThreshold()
    {
        var service = CreateService(_ => JsonResponse("""{"models":[]}"""));
        var belowThreshold = LocalProfile();
        belowThreshold.ConfiguredContextWindow =
            AiProviderService.ContextWarningThreshold - 1;
        var atThreshold = LocalProfile();
        atThreshold.ConfiguredContextWindow = AiProviderService.ContextWarningThreshold;
        var cloud = CloudProfile(AiProviderSource.OpenAi, "OPENAI_API_KEY");
        cloud.ConfiguredContextWindow = 8_192;

        var warning = service.GetContextWarning(belowThreshold);

        Assert.NotNull(warning);
        Assert.Equal(
            AiProviderService.ContextWarningThreshold - 1,
            warning.ConfiguredContextWindow);
        Assert.Equal(AiProviderService.ContextWarningThreshold, warning.WarningThreshold);
        Assert.Equal(AiProviderService.RecommendedContextWindow, warning.RecommendedContextWindow);
        Assert.Null(service.GetContextWarning(atThreshold));
        Assert.Null(service.GetContextWarning(cloud));
    }

    [Theory]
    [InlineData(AiProviderSource.Local, "qwen2.5-coder:32b", "qwen2.5-coder:32b")]
    [InlineData(AiProviderSource.OpenAi, "gpt-5", "openai/gpt-5")]
    [InlineData(AiProviderSource.Anthropic, "claude-sonnet-4", "anthropic/claude-sonnet-4")]
    [InlineData(AiProviderSource.Local, "OPENAI/qwen", "OPENAI/qwen")]
    public void QualifyModel_PreservesLocalIdsAndQualifiesCloudIds(
        AiProviderSource source,
        string model,
        string expected)
    {
        Assert.Equal(expected, AiProviderService.QualifyModel(source, model));
    }

    [Fact]
    public async Task DiscoverModelsAsync_ParsesOllamaTagsAndUsesLocalPlaceholder()
    {
        var requests = new ConcurrentQueue<RecordedRequest>();
        var service = CreateService(request =>
        {
            requests.Enqueue(Record(request));
            if (request.RequestUri!.AbsolutePath == "/api/show")
            {
                return JsonResponse(
                    """
                    {
                      "capabilities": ["completion", "tools", "thinking"],
                      "details": { "family": "qwen2" },
                      "model_info": { "qwen2.context_length": 131072 }
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "models": [
                    { "name": "qwen2.5-coder:32b" }
                  ]
                }
                """);
        });

        var result = await service.DiscoverModelsAsync(
            LocalProfile(),
            forceRefresh: true);

        Assert.Equal(ProviderHealthStatus.Healthy, result.HealthStatus);
        Assert.False(result.FromCache);
        var model = Assert.Single(result.Models);
        Assert.Equal("qwen2.5-coder:32b", model.Name);
        Assert.Equal("qwen2.5-coder:32b", model.Model);
        Assert.Equal(131_072, model.MaximumContextWindow);
        Assert.True(model.SupportsTools);
        Assert.True(model.ToolSupportKnown);
        Assert.True(model.SupportsReasoning);
        Assert.False(model.SupportsReasoningEffort);
        Assert.Equal(
            [
                "http://ollama.test:11434/api/tags",
                "http://ollama.test:11434/api/show",
            ],
            requests.Select(x => x.Uri).ToArray());
        Assert.All(requests, request =>
        {
            Assert.Equal("Bearer", request.Authorization?.Scheme);
            Assert.Equal(AiProviderService.LocalPlaceholderApiKey, request.Authorization?.Parameter);
        });
    }

    [Fact]
    public async Task DiscoverModelsAsync_IdentifiesOllamaModelWithoutToolCalling()
    {
        var service = CreateService(request =>
            request.RequestUri!.AbsolutePath == "/api/show"
                ? JsonResponse(
                    """
                    {
                      "capabilities": ["completion", "vision"],
                      "details": { "family": "gemma3" },
                      "model_info": { "gemma3.context_length": 131072 }
                    }
                    """)
                : JsonResponse("""{"models":[{"name":"gemma3:4b"}]}"""));

        var result = await service.DiscoverModelsAsync(
            LocalProfile(),
            forceRefresh: true);

        var model = Assert.Single(result.Models);
        Assert.Equal("gemma3:4b", model.Name);
        Assert.Equal(131_072, model.MaximumContextWindow);
        Assert.True(model.ToolSupportKnown);
        Assert.False(model.SupportsTools);
    }

    [Fact]
    public async Task DiscoverModelsAsync_InfersGptOssEffortFromStandardOllamaTags()
    {
        var service = CreateService(_ => JsonResponse(
            """
            {
              "models": [
                {
                  "name": "gpt-oss:20b",
                  "details": {
                    "family": "gptoss"
                  }
                }
              ]
            }
            """));

        var result = await service.DiscoverModelsAsync(
            LocalProfile(),
            forceRefresh: true);

        var model = Assert.Single(result.Models);
        Assert.Equal("gpt-oss:20b", model.Name);
        Assert.Null(model.MaximumContextWindow);
        Assert.False(model.SupportsTools);
        Assert.True(model.SupportsReasoning);
        Assert.True(model.SupportsReasoningEffort);
    }

    [Fact]
    public async Task DiscoverModelsAsync_FallsBackToOpenAiModelsEndpoint()
    {
        var requests = new ConcurrentQueue<RecordedRequest>();
        var service = CreateService(request =>
        {
            requests.Enqueue(Record(request));
            if (request.RequestUri!.AbsolutePath == "/api/tags")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return JsonResponse("""{"data":[{"id":"deepseek-coder-v2:latest"}]}""");
        });

        var result = await service.DiscoverModelsAsync(
            LocalProfile(),
            forceRefresh: true);

        Assert.Equal(ProviderHealthStatus.Healthy, result.HealthStatus);
        var model = Assert.Single(result.Models);
        Assert.Equal("deepseek-coder-v2:latest", model.Name);
        Assert.Equal("deepseek-coder-v2:latest", model.Model);
        Assert.Equal(
            [
                "http://ollama.test:11434/api/tags",
                "http://ollama.test:11434/v1/models",
            ],
            requests.Select(x => x.Uri).ToArray());
    }

    [Theory]
    [InlineData(LocalAiServerType.LmStudio)]
    [InlineData(LocalAiServerType.LlamaCpp)]
    public async Task DiscoverModelsAsync_OpenAiCompatibleServerTypesSkipOllamaTags(
        LocalAiServerType serverType)
    {
        var requests = new ConcurrentQueue<RecordedRequest>();
        var service = CreateService(request =>
        {
            requests.Enqueue(Record(request));
            return JsonResponse("""{"data":[{"id":"local-coder"}]}""");
        });
        var profile = LocalProfile(serverType: serverType);

        var result = await service.DiscoverModelsAsync(
            profile,
            forceRefresh: true);

        Assert.Equal(ProviderHealthStatus.Healthy, result.HealthStatus);
        var model = Assert.Single(result.Models);
        Assert.Equal("local-coder", model.Name);
        Assert.Equal("local-coder", model.Model);
        var request = Assert.Single(requests);
        Assert.Equal("http://ollama.test:11434/v1/models", request.Uri);
    }

    [Fact]
    public async Task DiscoverModelsAsync_CacheSeparatesLocalAiServerTypes()
    {
        var requests = new ConcurrentQueue<RecordedRequest>();
        var service = CreateService(request =>
        {
            requests.Enqueue(Record(request));
            return request.RequestUri!.AbsolutePath == "/api/tags"
                ? JsonResponse("""{"models":[{"name":"ollama-model"}]}""")
                : JsonResponse("""{"data":[{"id":"lm-studio-model"}]}""");
        });
        var profile = LocalProfile();

        var ollama = await service.DiscoverModelsAsync(profile);
        profile.LocalAiServerType = LocalAiServerType.LmStudio;
        var lmStudio = await service.DiscoverModelsAsync(profile);

        Assert.Equal("ollama-model", Assert.Single(ollama.Models).Name);
        Assert.Equal("lm-studio-model", Assert.Single(lmStudio.Models).Name);
        Assert.False(ollama.FromCache);
        Assert.False(lmStudio.FromCache);
        Assert.Equal(
            [
                "http://ollama.test:11434/api/tags",
                "http://ollama.test:11434/api/show",
                "http://ollama.test:11434/v1/models",
            ],
            requests.Select(x => x.Uri).ToArray());
    }

    [Fact]
    public void FindLocalModel_PrefersExactRawIdentifierOverLegacyPrefixFallback()
    {
        var raw = new AiProviderModel("foo", "foo");
        var exact = new AiProviderModel("openai/foo", "openai/foo");

        var result = AiProviderService.FindLocalModel(
            [raw, exact],
            "openai/foo");

        Assert.Same(exact, result);
    }

    [Fact]
    public void FindLocalModel_AcceptsLegacySyntheticOpenAiPrefix()
    {
        var raw = new AiProviderModel("foo", "foo");

        var result = AiProviderService.FindLocalModel(
            [raw],
            "openai/foo");

        Assert.Same(raw, result);
    }

    [Fact]
    public async Task DiscoverModelsAsync_ReportsReachableOllamaWithNoModelsAsHealthy()
    {
        var requests = new ConcurrentQueue<RecordedRequest>();
        var service = CreateService(request =>
        {
            requests.Enqueue(Record(request));
            return JsonResponse("""{"models":[]}""");
        });

        var result = await service.DiscoverModelsAsync(
            LocalProfile(),
            forceRefresh: true);

        Assert.Equal(ProviderHealthStatus.Healthy, result.HealthStatus);
        Assert.Empty(result.Models);
        Assert.Single(requests);
        Assert.Equal("http://ollama.test:11434/api/tags", requests.Single().Uri);
    }

    [Fact]
    public async Task DiscoverModelsAsync_ReportsOfflineWhenEndpointsCannotBeReached()
    {
        var requestCount = 0;
        var service = CreateService(_ =>
        {
            Interlocked.Increment(ref requestCount);
            throw new HttpRequestException("connection refused");
        });

        var result = await service.DiscoverModelsAsync(
            LocalProfile(),
            forceRefresh: true);

        Assert.Equal(ProviderHealthStatus.Offline, result.HealthStatus);
        Assert.Empty(result.Models);
        Assert.Contains("connection refused", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task DiscoverModelsAsync_ReportsTimeout()
    {
        var profile = LocalProfile();
        profile.ModelDiscoveryMode = ModelDiscoveryMode.OpenAi;
        var service = CreateService(_ => throw new TaskCanceledException("simulated timeout"));
        var stopwatch = Stopwatch.StartNew();

        var result = await service.DiscoverModelsAsync(profile, forceRefresh: true);

        stopwatch.Stop();
        Assert.Equal(ProviderHealthStatus.Offline, result.HealthStatus);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DiscoverModelsAsync_RejectsOversizedCatalog()
    {
        var profile = LocalProfile();
        profile.ModelDiscoveryMode = ModelDiscoveryMode.OpenAi;
        var oversizedModel = JsonSerializer.Serialize(new string('x', 2 * 1024 * 1024));
        var service = CreateService(_ => JsonResponse(
            "{\"data\":[{\"id\":" + oversizedModel + "}]}"));

        var result = await service.DiscoverModelsAsync(profile, forceRefresh: true);

        Assert.Equal(ProviderHealthStatus.Offline, result.HealthStatus);
        Assert.Contains("2 MiB", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Models);
    }

    private static AiProviderService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHttpClientFactory(new StubHttpMessageHandler(
            (request, _) => Task.FromResult(responder(request)))));

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static AiProviderProfile LocalProfile(
        string baseUrl = "http://ollama.test:11434/v1",
        LocalAiServerType serverType = LocalAiServerType.Ollama) =>
        new()
        {
            Name = "Local Ollama",
            Source = AiProviderSource.Local,
            LocalAiServerType = serverType,
            BaseUrl = baseUrl,
            ModelDiscoveryMode = ModelDiscoveryMode.Auto,
            MaximumConcurrency = 1,
            ConfiguredContextWindow = AiProviderService.RecommendedContextWindow,
        };

    private static AiProviderProfile CloudProfile(
        AiProviderSource source,
        string? apiKeyEnvironmentVariable) =>
        new()
        {
            Name = source.ToString(),
            Source = source,
            BaseUrl = source == AiProviderSource.Anthropic
                ? "https://api.anthropic.com"
                : "https://api.openai.com/v1",
            ApiKeyEnvironmentVariable = apiKeyEnvironmentVariable,
            Enabled = false,
            MaximumConcurrency = 1,
        };

    private static RecordedRequest Record(HttpRequestMessage request) =>
        new(request.RequestUri!.AbsoluteUri, request.Headers.Authorization);

    private sealed record RecordedRequest(
        string Uri,
        AuthenticationHeaderValue? Authorization);

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler);

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }
}
