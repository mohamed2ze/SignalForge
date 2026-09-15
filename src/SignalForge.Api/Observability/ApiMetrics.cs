using Prometheus;

namespace SignalForge.Api.Observability;

/// <summary>
/// Application-level Prometheus counters for the API process. Instrumentation is wired at the
/// request-layer touch points (event ingestion, rate-limit rejection) so the numbers reflect real
/// traffic; the default registry is served on <c>/metrics</c> along with the built-in HTTP metrics.
/// </summary>
public static class ApiMetrics
{
    /// <summary>Events accepted by POST /api/events (newly created or idempotent replay).</summary>
    public static readonly Counter EventsIngested = Metrics.CreateCounter(
        "signalforge_events_ingested_total",
        "Total events ingested successfully through POST /api/events (new or idempotent replay).");

    /// <summary>Requests rejected with HTTP 429 by the API-key rate limiter.</summary>
    public static readonly Counter RateLimitRejections = Metrics.CreateCounter(
        "signalforge_rate_limit_rejections_total",
        "Requests rejected with HTTP 429 by the per-API-key rate limiter.");
}