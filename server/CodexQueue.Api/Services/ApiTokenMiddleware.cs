namespace CodexQueue.Api.Services;

public sealed class ApiTokenMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private readonly string? _token =
        configuration["CQ_API_TOKEN"]
        ?? configuration["Security:ApiToken"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(_token)
            || !context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/api/health")
            || context.Request.Path.StartsWithSegments("/api/config"))
        {
            await next(context);
            return;
        }

        var supplied = ReadToken(context);
        if (!string.Equals(supplied, _token, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "API token is required." });
            return;
        }

        await next(context);
    }

    private static string? ReadToken(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("Authorization", out var authorization))
        {
            var value = authorization.ToString();
            const string prefix = "Bearer ";
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return value[prefix.Length..].Trim();
            }
        }

        return context.Request.Headers.TryGetValue("X-CQ-Token", out var token)
            ? token.ToString()
            : null;
    }
}
