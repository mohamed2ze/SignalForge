using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace SignalForge.Api.Middleware;

/// <summary>
/// Fixed-window rate limiter keyed by the authenticated API key. Buckets are per-<c>api_key_id</c>
/// so each key owns its budget and a single compromised/leaked key cannot saturate the API;
/// unauthenticated traffic shares one anonymous bucket. Health probes and any path that must
/// never be throttled (liveness/readiness for orchestrators) are exempted.
///
/// Uses the in-process <see cref="PartitionedRateLimiter{TResource}"/> (fixed-window). For a
/// multi-instance deployment the window is enforced per process; a distributed store would be the
/// next step if a hard cross-node cap is required.
/// </summary>
public sealed class ApiKeyRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly PartitionedRateLimiter<HttpContext> _limiter;

    public ApiKeyRateLimitMiddleware(
        RequestDelegate next,
        IOptions<ApiKeyRateLimitOptions> options)
    {
        _next = next;

        var settings = options.Value;
        var window = TimeSpan.FromSeconds(Math.Max(1, settings.WindowSeconds));
        var permitLimit = Math.Max(1, settings.PermitLimit);

        // The fixed window is shared across every request to the same key within the window.
        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.User.FindFirstValue("api_key_id") ?? "anonymous",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueLimit = 0
                }));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/health/live") || path.StartsWithSegments("/health/ready"))
        {
            await _next(context);
            return;
        }

        using var lease = await _limiter.AcquireAsync(context, cancellationToken: context.RequestAborted);

        if (!lease.IsAcquired)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        await _next(context);
    }
}