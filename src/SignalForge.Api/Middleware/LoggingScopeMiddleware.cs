using System.Diagnostics;

namespace SignalForge.Api.Middleware;

/// <summary>
/// Enriches every request's log lines with a stable correlation id (HTTP trace identifier) and,
/// when present, the authenticated tenant and API-key ids. The scoped properties are emitted
/// verbatim into structured/JSON log output under the names correlationId / tenantId / apiKeyId.
/// Runs after authentication so identity claims are available, before authorization/controllers.
/// </summary>
public class LoggingScopeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<LoggingScopeMiddleware> _logger;

    public LoggingScopeMiddleware(RequestDelegate next, ILogger<LoggingScopeMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var scopeItems = new Dictionary<string, object?>(3)
        {
            ["correlationId"] = context.TraceIdentifier
        };

        var tenantClaim = context.User.FindFirst("tenant_id")?.Value;
        if (tenantClaim is not null)
            scopeItems["tenantId"] = tenantClaim;

        var apiKeyClaim = context.User.FindFirst("api_key_id")?.Value;
        if (apiKeyClaim is not null)
            scopeItems["apiKeyId"] = apiKeyClaim;

        using (_logger.BeginScope(scopeItems))
        {
            var stopwatch = Stopwatch.StartNew();
            await _next(context);
            stopwatch.Stop();

            _logger.LogInformation(
                "HTTP {Method} {Path} completed in {ElapsedMs}ms with status {StatusCode}",
                context.Request.Method,
                context.Request.Path,
                stopwatch.ElapsedMilliseconds,
                context.Response.StatusCode);
        }
    }
}