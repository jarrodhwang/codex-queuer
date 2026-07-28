using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Services;

public sealed record AiProviderModel(
    string Name,
    string Model,
    int? MaximumContextWindow = null,
    bool SupportsTools = false,
    bool SupportsReasoning = false,
    bool SupportsReasoningEffort = false);

public sealed record AiProviderContextWarning(
    int ConfiguredContextWindow,
    int WarningThreshold,
    int RecommendedContextWindow,
    string Message);

public sealed record AiProviderValidationResult(
    bool IsValid,
    string? NormalizedBaseUrl,
    string? NormalizedDefaultModel,
    IReadOnlyList<string> Errors,
    AiProviderContextWarning? ContextWarning);

public sealed record AiProviderDiscoveryResult(
    ProviderHealthStatus HealthStatus,
    DateTimeOffset CheckedAt,
    IReadOnlyList<AiProviderModel> Models,
    string? Error,
    bool FromCache);

public interface IAiProviderService
{
    AiProviderValidationResult Validate(AiProviderProfile profile);

    AiProviderContextWarning? GetContextWarning(AiProviderProfile profile);

    void ApplyHealth(AiProviderProfile profile, AiProviderDiscoveryResult result);

    Task<AiProviderDiscoveryResult> DiscoverModelsAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false);
}

// Secret resolution is deliberately assembly-internal. API contracts should expose only
// the configured environment-variable reference, never the resolved value.
internal interface IAiProviderSecretResolver
{
    string ResolveApiKeyForExecution(AiProviderProfile profile);
}

public sealed class AiProviderService(IHttpClientFactory httpClientFactory)
    : IAiProviderService, IAiProviderSecretResolver
{
    public const string LocalPlaceholderApiKey = "local-llm";
    public const string LocalApiKeyPlaceholder = LocalPlaceholderApiKey;
    // Local Codex runs need a large project prompt window. 32K is the supported
    // minimum; 64K remains the preferred default for multi-step work.
    public const int ContextWarningThreshold = 32_768;
    public const int RecommendedContextWindow = 65_536;
    private const int MaximumModelCatalogBytes = 2 * 1024 * 1024;
    private const int MaximumDiscoveredModels = 1_000;
    private const int MaximumModelIdentifierLength = 256;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(20);
    private static readonly Regex EnvironmentVariableNameRegex = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly ConcurrentDictionary<string, AiProviderDiscoveryResult> _cache =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _discoveryLocks =
        new(StringComparer.Ordinal);

    public AiProviderValidationResult Validate(AiProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var errors = new List<string>();
        if (!Enum.IsDefined(typeof(AiProviderSource), profile.Source))
        {
            errors.Add("Provider source is invalid.");
        }

        if (!Enum.IsDefined(typeof(LocalAiServerType), profile.LocalAiServerType))
        {
            errors.Add("Local AI server type is invalid.");
        }

        if (!Enum.IsDefined(typeof(ModelDiscoveryMode), profile.ModelDiscoveryMode))
        {
            errors.Add("Model discovery mode is invalid.");
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            errors.Add("Provider profile name is required.");
        }
        else if (profile.Name.Trim().Length > 160)
        {
            errors.Add("Provider profile name must be 160 characters or fewer.");
        }

        string? normalizedBaseUrl = null;
        if (profile.BaseUrl?.Trim().Length > 2048)
        {
            errors.Add("Provider base URL must be 2,048 characters or fewer.");
        }
        if (!TryNormalizeBaseUrl(profile.Source, profile.BaseUrl, out normalizedBaseUrl, out var baseUrlError))
        {
            errors.Add(baseUrlError!);
        }

        var secretReference = profile.ApiKeyEnvironmentVariable?.Trim();
        if (!string.IsNullOrWhiteSpace(secretReference)
            && (secretReference.Length > 160 || !EnvironmentVariableNameRegex.IsMatch(secretReference)))
        {
            errors.Add("API key environment-variable reference is invalid.");
        }
        else if (profile.Source == AiProviderSource.Local
                 && !string.IsNullOrWhiteSpace(secretReference))
        {
            errors.Add(
                "Local AI server profiles must not configure an API key environment-variable reference in this release.");
        }
        else if (profile.Source != AiProviderSource.Local && string.IsNullOrWhiteSpace(secretReference))
        {
            errors.Add("A cloud provider requires an API key environment-variable reference.");
        }

        if (profile.Source != AiProviderSource.Local && profile.Enabled)
        {
            errors.Add(
                "Cloud provider profiles cannot be enabled in this Local-only release.");
        }

        if (profile.MaximumConcurrency < 1)
        {
            errors.Add("Maximum concurrency must be at least one.");
        }

        if (profile.ConfiguredContextWindow is <= 0)
        {
            errors.Add("Configured context window must be greater than zero.");
        }

        string? normalizedDefaultModel = null;
        if (!string.IsNullOrWhiteSpace(profile.DefaultModel))
        {
            if (!IsSafeModelIdentifier(profile.DefaultModel))
            {
                errors.Add("Default model identifier must be 256 characters or fewer and contain no control characters.");
            }
            else
            {
                normalizedDefaultModel = QualifyModel(profile.Source, profile.DefaultModel);
            }
        }

        return new AiProviderValidationResult(
            errors.Count == 0,
            normalizedBaseUrl,
            normalizedDefaultModel,
            errors,
            GetContextWarning(profile));
    }

    public AiProviderContextWarning? GetContextWarning(AiProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Source != AiProviderSource.Local
            || profile.ConfiguredContextWindow is not { } configuredContextWindow
            || configuredContextWindow >= ContextWarningThreshold)
        {
            return null;
        }

        return new AiProviderContextWarning(
            configuredContextWindow,
            ContextWarningThreshold,
            RecommendedContextWindow,
            "The configured context window is below the "
            + ContextWarningThreshold.ToString("N0", CultureInfo.InvariantCulture)
            + "-token minimum required for reliable Local Codex project prompts.");
    }

    public void ApplyHealth(AiProviderProfile profile, AiProviderDiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(result);
        profile.LastHealthStatus = result.HealthStatus;
        profile.LastHealthAt = result.CheckedAt;
        profile.LastHealthError = result.Error is { Length: > 2048 }
            ? result.Error[..2048]
            : result.Error;
    }

    public async Task<AiProviderDiscoveryResult> DiscoverModelsAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // The first vertical slice intentionally performs discovery only against
        // unauthenticated Local AI server profiles. Never resolve or transmit an
        // environment-referenced cloud credential from this browser-triggerable path.
        if (profile.Source != AiProviderSource.Local)
        {
            return Offline(
                "Cloud provider model discovery is disabled in this Local-only release.");
        }

        var validation = Validate(profile);
        if (!validation.IsValid || validation.NormalizedBaseUrl is null)
        {
            return Offline(string.Join(" ", validation.Errors));
        }

        if (!profile.Enabled)
        {
            return Offline("Provider profile is disabled.");
        }

        var cacheKey = BuildCacheKey(profile, validation.NormalizedBaseUrl);
        if (!forceRefresh && TryGetCached(cacheKey, out var cached))
        {
            return cached;
        }

        var discoveryLock = _discoveryLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await discoveryLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && TryGetCached(cacheKey, out cached))
            {
                return cached;
            }

            var result = await DiscoverUncachedAsync(
                profile,
                validation.NormalizedBaseUrl,
                cancellationToken);
            _cache[cacheKey] = result;
            return result;
        }
        finally
        {
            discoveryLock.Release();
        }
    }

    public static bool TryNormalizeBaseUrl(
        AiProviderSource source,
        string? value,
        out string normalized,
        out string? error)
    {
        normalized = "";
        error = null;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "Provider base URL must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "Provider base URL must not contain embedded credentials.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Provider base URL must not contain a query string or fragment.";
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (source == AiProviderSource.Local)
        {
            if (path.Length == 0)
            {
                path = "/v1";
            }
            else if (!string.Equals(path, "/v1", StringComparison.OrdinalIgnoreCase))
            {
                error = "A Local AI server base URL must use the /v1 endpoint.";
                return false;
            }
            else
            {
                path = "/v1";
            }
        }

        var builder = new UriBuilder(uri)
        {
            Path = path.Length == 0 ? "/" : path,
            Query = "",
            Fragment = ""
        };
        normalized = builder.Uri.AbsoluteUri.TrimEnd('/');
        return true;
    }

    public static string QualifyModel(AiProviderSource source, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var normalized = model.Trim();
        if (!IsSafeModelIdentifier(normalized))
        {
            throw new ArgumentException(
                "Model identifier must be 256 characters or fewer and contain no control characters.",
                nameof(model));
        }

        if (source == AiProviderSource.Local)
        {
            return normalized;
        }

        var prefix = source == AiProviderSource.Anthropic ? "anthropic/" : "openai/";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? prefix + normalized[prefix.Length..]
            : prefix + normalized;
    }

    public static AiProviderModel? FindLocalModel(
        IEnumerable<AiProviderModel> models,
        string selectedModel)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedModel);
        var normalized = selectedModel.Trim();

        var exact = models.FirstOrDefault(model =>
            string.Equals(model.Model, normalized, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        const string legacyPrefix = "openai/";
        if (!normalized.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase)
            || normalized.Length == legacyPrefix.Length)
        {
            return null;
        }

        var legacyRawModel = normalized[legacyPrefix.Length..];
        return models.FirstOrDefault(model =>
            string.Equals(model.Model, legacyRawModel, StringComparison.Ordinal));
    }

    string IAiProviderSecretResolver.ResolveApiKeyForExecution(AiProviderProfile profile) =>
        ResolveApiKeyForExecution(profile);

    internal static string ResolveApiKeyForExecution(AiProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var reference = profile.ApiKeyEnvironmentVariable?.Trim();
        if (profile.Source == AiProviderSource.Local)
        {
            if (!string.IsNullOrWhiteSpace(reference))
            {
                throw new InvalidOperationException(
                    "Authenticated Local AI server profiles are not supported in this release.");
            }

            return LocalPlaceholderApiKey;
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new InvalidOperationException(
                "The provider profile does not configure an API key environment-variable reference.");
        }

        if (!EnvironmentVariableNameRegex.IsMatch(reference))
        {
            throw new InvalidOperationException(
                "The provider profile API key environment-variable reference is invalid.");
        }

        var value = Environment.GetEnvironmentVariable(reference);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "The configured provider API key environment variable is not set.");
        }

        return value;
    }

    private async Task<AiProviderDiscoveryResult> DiscoverUncachedAsync(
        AiProviderProfile profile,
        string normalizedBaseUrl,
        CancellationToken cancellationToken)
    {
        var attempts = new List<DiscoveryAttempt>();
        if (profile.LocalAiServerType == LocalAiServerType.Ollama
            && profile.ModelDiscoveryMode != ModelDiscoveryMode.OpenAi
            && profile.Source == AiProviderSource.Local)
        {
            attempts.Add(await ReadModelsAsync(
                profile,
                BuildOllamaTagsUri(normalizedBaseUrl),
                ParseOllamaTags,
                cancellationToken));

            if (attempts[^1].Reachable)
            {
                return Healthy(attempts[^1].Models);
            }

            if (profile.ModelDiscoveryMode == ModelDiscoveryMode.Ollama)
            {
                return Offline(
                    attempts[^1].Error
                    ?? "Ollama /api/tags did not return a readable model catalog.");
            }
        }

        attempts.Add(await ReadModelsAsync(
            profile,
            new Uri(normalizedBaseUrl.TrimEnd('/') + "/models", UriKind.Absolute),
            ParseOpenAiModels,
            cancellationToken));
        if (attempts[^1].Reachable)
        {
            return Healthy(attempts[^1].Models);
        }

        var error = string.Join(
            " ",
            attempts
                .Select(x => x.Error)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal));
        return Offline(string.IsNullOrWhiteSpace(error)
            ? "The provider did not return a readable model catalog."
            : error);
    }

    private async Task<DiscoveryAttempt> ReadModelsAsync(
        AiProviderProfile profile,
        Uri uri,
        Func<JsonElement, IReadOnlyList<DiscoveredModel>> parser,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                ResolveApiKeyForExecution(profile));

            var client = httpClientFactory.CreateClient(nameof(AiProviderService));
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return DiscoveryAttempt.Failed(
                    $"Provider model discovery returned HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength is > MaximumModelCatalogBytes)
            {
                return DiscoveryAttempt.Failed("Provider model catalog exceeded the 2 MiB response limit.");
            }

            await response.Content.LoadIntoBufferAsync(MaximumModelCatalogBytes, timeout.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: timeout.Token);
            var models = parser(document.RootElement)
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .Select(x => x with { Name = x.Name.Trim() })
                .Where(x => IsSafeModelIdentifier(x.Name))
                .GroupBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => new DiscoveredModel(
                    x.Key,
                    x.Max(model => model.MaximumContextWindow),
                    x.Any(model => model.SupportsTools),
                    x.Any(model => model.SupportsReasoning),
                    x.Any(model => model.SupportsReasoningEffort)))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumDiscoveredModels)
                .Select(x => new AiProviderModel(
                    x.Name,
                    QualifyModel(profile.Source, x.Name),
                    x.MaximumContextWindow,
                    x.SupportsTools,
                    x.SupportsReasoning,
                    x.SupportsReasoningEffort))
                .ToArray();
            return DiscoveryAttempt.Succeeded(models);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DiscoveryAttempt.Failed("Provider model discovery timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            return DiscoveryAttempt.Failed(
                ex is InvalidOperationException
                    ? ex.Message
                    : "Provider model discovery failed: " + ex.Message);
        }
    }

    private bool TryGetCached(string cacheKey, out AiProviderDiscoveryResult result)
    {
        if (_cache.TryGetValue(cacheKey, out var cached)
            && DateTimeOffset.UtcNow - cached.CheckedAt < CacheDuration)
        {
            result = cached with { FromCache = true };
            return true;
        }

        _cache.TryRemove(cacheKey, out _);
        result = null!;
        return false;
    }

    private static Uri BuildOllamaTagsUri(string normalizedBaseUrl)
    {
        var baseUri = new Uri(normalizedBaseUrl, UriKind.Absolute);
        return new UriBuilder(baseUri)
        {
            Path = "/api/tags",
            Query = "",
            Fragment = ""
        }.Uri;
    }

    private static IReadOnlyList<DiscoveredModel> ParseOllamaTags(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Ollama /api/tags did not return a models array.");
        }

        return models.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x =>
            {
                var name = ReadString(x, "name") ?? ReadString(x, "model") ?? "";
                var capabilities = ReadStringArray(x, "capabilities");
                var family = ReadNestedString(x, "details", "family");
                var supportsReasoningEffort =
                    string.Equals(family, "gptoss", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("gpt-oss", StringComparison.OrdinalIgnoreCase);
                var supportsReasoning = supportsReasoningEffort
                    || capabilities.Contains(
                        "thinking",
                        StringComparer.OrdinalIgnoreCase);
                return new DiscoveredModel(
                    name,
                    ReadPositiveInt32(x, "details", "context_length"),
                    capabilities.Contains("tools", StringComparer.OrdinalIgnoreCase),
                    supportsReasoning,
                    supportsReasoningEffort);
            })
            .ToArray();
    }

    private static IReadOnlyList<DiscoveredModel> ParseOpenAiModels(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("OpenAI-compatible /v1/models did not return a data array.");
        }

        return models.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Object)
            .Select(x => ReadString(x, "id") ?? ReadString(x, "name"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x =>
            {
                var name = x!;
                var supportsReasoningEffort = name.Contains(
                    "gpt-oss",
                    StringComparison.OrdinalIgnoreCase);
                return new DiscoveredModel(
                    name,
                    SupportsReasoning: supportsReasoningEffort,
                    SupportsReasoningEffort: supportsReasoningEffort);
            })
            .ToArray();
    }

    private static string BuildCacheKey(AiProviderProfile profile, string normalizedBaseUrl) =>
        profile.Id
        + "|" + profile.Source
        + "|" + profile.LocalAiServerType
        + "|" + profile.ModelDiscoveryMode
        + "|" + profile.ApiKeyEnvironmentVariable
        + "|" + normalizedBaseUrl;

    private static bool IsSafeModelIdentifier(string value)
    {
        var normalized = value.Trim();
        return normalized.Length is > 0 and <= MaximumModelIdentifierLength
            && !normalized.Any(char.IsControl);
    }

    private static AiProviderDiscoveryResult Healthy(IReadOnlyList<AiProviderModel> models) =>
        new(ProviderHealthStatus.Healthy, DateTimeOffset.UtcNow, models, null, false);

    private static AiProviderDiscoveryResult Offline(string error) =>
        new(
            ProviderHealthStatus.Offline,
            DateTimeOffset.UtcNow,
            Array.Empty<AiProviderModel>(),
            error,
            false);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement element,
        string propertyName) =>
        element.TryGetProperty(propertyName, out var values)
        && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToArray()
            : [];

    private static string? ReadNestedString(
        JsonElement element,
        string objectPropertyName,
        string valuePropertyName) =>
        element.TryGetProperty(objectPropertyName, out var nested)
        && nested.ValueKind == JsonValueKind.Object
        && nested.TryGetProperty(valuePropertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadPositiveInt32(
        JsonElement element,
        string objectPropertyName,
        string valuePropertyName)
    {
        if (!element.TryGetProperty(objectPropertyName, out var nested)
            || nested.ValueKind != JsonValueKind.Object
            || !nested.TryGetProperty(valuePropertyName, out var value)
            || !value.TryGetInt32(out var parsed)
            || parsed <= 0)
        {
            return null;
        }

        return parsed;
    }

    private sealed record DiscoveredModel(
        string Name,
        int? MaximumContextWindow = null,
        bool SupportsTools = false,
        bool SupportsReasoning = false,
        bool SupportsReasoningEffort = false);

    private sealed record DiscoveryAttempt(
        bool Reachable,
        IReadOnlyList<AiProviderModel> Models,
        string? Error)
    {
        public static DiscoveryAttempt Succeeded(IReadOnlyList<AiProviderModel> models) =>
            new(true, models, null);

        public static DiscoveryAttempt Failed(string error) =>
            new(false, Array.Empty<AiProviderModel>(), error);
    }
}
