using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SignalForge.Application.Data;
using SignalForge.Application.RateLimiting;

namespace SignalForge.Infrastructure.RateLimiting;

/// <summary>
/// Cross-node <see cref="IRateLimiter"/> backed by the shared <c>RateLimitCounters</c> table. Because
/// every API instance derives window keys from wall-clock time and atomically increments the same
/// row, <c>RateLimiting:Store=Sql</c> enforces a single budget across the whole deployment — the
/// in-process limiter's per-instance counting replaced. The increment is one UPDATE; only the first
/// request in a window (row absent) pays an INSERT.
/// </summary>
public sealed class SqlRateLimiter : IRateLimiter
{
    private readonly IServiceProvider _serviceProvider;

    public SqlRateLimiter(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public async Task<long> IncrementAsync(
        string partitionKey,
        long windowKey,
        CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();

        // Fast path: the (partition, window) row already exists — bump its counter in place.
        // ExecuteUpdateAsync reports how many rows matched; zero means this is the first request
        // of the window and the row still needs to be created.
        var touched = await Counters(db, partitionKey, windowKey)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(r => r.Count, r => r.Count + 1),
                cancellationToken);

        if (touched == 0)
            return await InsertFirstAsync(db, partitionKey, windowKey, cancellationToken);

        // Re-read to answer with the post-increment count (UPDATE ... OUTPUT would save a
        // round-trip, but this stays provider-neutral).
        return await Counters(db, partitionKey, windowKey)
            .Select(r => r.Count)
            .SingleAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<long> GetCountAsync(
        string partitionKey,
        long windowKey,
        CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();

        return await Counters(db, partitionKey, windowKey)
            .Select(r => r.Count)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static async Task<long> InsertFirstAsync(
        ISignalForgeDbContext db,
        string partitionKey,
        long windowKey,
        CancellationToken cancellationToken)
    {
        // Two nodes can both miss the row and race the INSERT; the loser trips the unique
        // (partition, window) constraint and simply retries the increment on the now-present row.
        db.RateLimitCounters.Add(new RateLimitCounter(partitionKey, windowKey, 1));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return 1;
        }
        catch (DbUpdateException)
        {
            await Counters(db, partitionKey, windowKey)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(r => r.Count, r => r.Count + 1),
                    cancellationToken);
            return await Counters(db, partitionKey, windowKey)
                .Select(r => r.Count)
                .SingleAsync(cancellationToken);
        }
    }

    private static IQueryable<RateLimitCounter> Counters(
        ISignalForgeDbContext db,
        string partitionKey,
        long windowKey)
        => db.RateLimitCounters.Where(r => r.PartitionKey == partitionKey && r.WindowKey == windowKey);
}