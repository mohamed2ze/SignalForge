using System.Security.Claims;
using Microsoft.Extensions.Options;
using SignalForge.Api.Observability;
using SignalForge.Application.RateLimiting;

namespace SignalForge.Api.Middleware;

/// <summary>
/// Fixed-window rate limiter keyed by the authenticated API key. Buckets are per-<c>api_key_id</c>
/// so each key owns its budget and a single compromised/leaked key cannot saturate the API;
/// unauthenticated traffic shares one anonymous bucket. Health probes and any path that must
/// never be throttled (liveness/readiness for orchestrators) are exempted.
///
/// The counter itself lives behind <see cref="IRateLimiter"/>: the default store is in-process
/// (per instance), while <c>RateLimiting:Store=Sql</c> switches to a shared SQL table so every
/// API instance enforces one global budget. See <c>ApiKeyRateLimitOptions</c> for the knobs.
/// </summary>
public sealed class ApiKeyRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRateLimiter _limiter;
    private readonly TimeSpan _window;
    private readonly long _permitLimit;

    public ApiKeyRateLimitMiddleware(
        RequestDelegate next,
        IRateLimiter limiter,
        IOptions<ApiKeyRateLimitOptions> options)
    {
        _next = next;
        _limiter = limiter;
        _window = TimeSpan.FromSeconds(Math.Max(1, options.Value.WindowSeconds));
        _permitLimit = Math.Max(1, options.Value.PermitLimit);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/health/live") || path.StartsWithSegments("/health/ready"))
        {
            await _next(context);
            return;
        }

        var partitionKey = context.User.FindFirstValue("api_key_id") ?? "anonymous";
        var windowKey = RateLimitWindow.GetWindowKey(DateTimeOffset.UtcNow, _window);

        var count = await _limiter.IncrementAsync(partitionKey, windowKey, context.RequestAborted);

        if (count > _permitLimit)
        {
            ApiMetrics.RateLimitRejections.Inc();
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        await _next(context);
    }
}