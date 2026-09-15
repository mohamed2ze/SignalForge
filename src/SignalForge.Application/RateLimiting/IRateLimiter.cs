namespace SignalForge.Application.RateLimiting;

/// <summary>
/// Atomic fixed-window counter store behind the API-key rate limiter. Implementations increment a
/// per-(partition key, window) counter and return the updated count. The window key is computed by
/// the caller from system time (in-process or across nodes), so all instances of a deployment share
/// one budget when they share a store (SQL as the built-in distributed store; Redis could be
/// swapped in behind the same port).
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Atomically increments the counter for <paramref name="partitionKey"/> / <paramref name="windowKey"/>
    /// and returns the new count.
    /// </summary>
    Task<long> IncrementAsync(
        string partitionKey,
        long windowKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current count for <paramref name="partitionKey"/> / <paramref name="windowKey"/>
    /// without incrementing. Used by tests and health/metadata surfaces; the request path only needs
    /// <see cref="IncrementAsync"/>.
    /// </summary>
    Task<long> GetCountAsync(
        string partitionKey,
        long windowKey,
        CancellationToken cancellationToken = default);
}