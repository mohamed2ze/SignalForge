namespace SignalForge.Api.Middleware;

/// <summary>
/// Configuration section and settings for the API-key rate limiter.
/// </summary>
public class ApiKeyRateLimitOptions
{
    /// <summary>Configuration section carrying the rate-limit budget: <c>RateLimiting</c>.</summary>
    public const string SectionName = "RateLimiting";

    /// <summary>
    /// Maximum requests a single API key may issue per <see cref="Window"/>.
    /// </summary>
    public int PermitLimit { get; set; } = 1000;

    /// <summary>
    /// Sliding/fixed window length in seconds. The default limiter replenishes the budget once per
    /// window (fixed-window semantics).
    /// </summary>
    public int WindowSeconds { get; set; } = 60;
}