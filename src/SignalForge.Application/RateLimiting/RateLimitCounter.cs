namespace SignalForge.Application.RateLimiting;

/// <summary>
/// Single fixed-window counter row backing the SQL <see cref="IRateLimiter"/> store (table
/// <c>RateLimitCounters</c>). Keyed by (partition key, window key) so each API key owns one row per
/// active fixed window across every API instance.
/// </summary>
public sealed class RateLimitCounter
{
    public RateLimitCounter(string partitionKey, long windowKey, long count)
    {
        PartitionKey = partitionKey;
        WindowKey = windowKey;
        Count = count;
    }

    /// <summary>Rate-limit partition (authenticated <c>api_key_id</c>, or "anonymous").</summary>
    public string PartitionKey { get; set; }

    /// <summary>Fixed-window bucket computed by <see cref="RateLimitWindow.GetWindowKey"/>.</summary>
    public long WindowKey { get; set; }

    /// <summary>Current request count in this window.</summary>
    public long Count { get; set; }
}