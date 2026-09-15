using System.Collections.Concurrent;
using SignalForge.Application.RateLimiting;

namespace SignalForge.Infrastructure.RateLimiting;

/// <summary>
/// In-process <see cref="IRateLimiter"/>: a lock-free fixed-window counter keyed by (partition,
/// window). Cheap and the default for single-instance deployments. Stale windows are pruned on
/// rollover so the dictionary stays bounded by (partitions × live windows) rather than growing
/// with time. Per-process only — see <see cref="SqlRateLimiter"/> for a cross-node store.
/// </summary>
public sealed class InMemoryRateLimiter : IRateLimiter
{
    private readonly ConcurrentDictionary<(string PartitionKey, long WindowKey), long> _counters = new();

    /// <inheritdoc />
    public Task<long> IncrementAsync(
        string partitionKey,
        long windowKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var count = _counters.AddOrUpdate(
            (partitionKey, windowKey),
            addValue: 1,
            updateValueFactory: (_, existing) => checked(existing + 1));

        Prune(windowKey);
        return Task.FromResult(count);
    }

    /// <inheritdoc />
    public Task<long> GetCountAsync(
        string partitionKey,
        long windowKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _counters.TryGetValue((partitionKey, windowKey), out var count);
        return Task.FromResult(count);
    }

    /// <summary>
    /// Drops buckets from windows that rolled over before <paramref name="currentWindowKey"/>.
    /// A bucket is only incremented for the window computed from the current time, so any old
    /// window is already final; pruning it cannot affect future counts. Best-effort: a torn read
    /// here only leaks a stale entry, never a wrong count.
    /// </summary>
    private void Prune(long currentWindowKey)
    {
        foreach (var (key, windowKey) in _counters.Keys)
        {
            if (windowKey < currentWindowKey)
                _counters.TryRemove((key, windowKey), out _);
        }
    }
}