using SignalForge.Application.RateLimiting;
using SignalForge.Infrastructure.RateLimiting;

namespace SignalForge.UnitTests.RateLimiting;

/// <summary>
/// Behavior pins for the default in-process <see cref="InMemoryRateLimiter"/>: atomic increments
/// per (partition, window), independent budgets per partition, and a fresh budget once the window
/// rolls over.
/// </summary>
public sealed class InMemoryRateLimiterTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Increments_Are_Atomic_Per_Partition_And_Window()
    {
        var limiter = new InMemoryRateLimiter();
        var windowKey = RateLimitWindow.GetWindowKey(DateTimeOffset.UtcNow, Window);

        var tasks = Enumerable.Range(0, 100)
            .Select(_ => limiter.IncrementAsync("tenant-A", windowKey))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(100, await limiter.GetCountAsync("tenant-A", windowKey));
    }

    [Fact]
    public async Task Each_Partition_Owns_Its_Own_Budget()
    {
        var limiter = new InMemoryRateLimiter();
        var windowKey = RateLimitWindow.GetWindowKey(DateTimeOffset.UtcNow, Window);

        await limiter.IncrementAsync("key-A", windowKey);
        await limiter.IncrementAsync("key-A", windowKey);
        await limiter.IncrementAsync("key-B", windowKey);

        Assert.Equal(2, await limiter.GetCountAsync("key-A", windowKey));
        Assert.Equal(1, await limiter.GetCountAsync("key-B", windowKey));
    }

    [Fact]
    public async Task Old_Windows_Are_Pruned_And_Do_Not_Count()
    {
        var limiter = new InMemoryRateLimiter();
        var oldWindow = RateLimitWindow.GetWindowKey(DateTimeOffset.UtcNow, Window) - Window.Ticks;

        await limiter.IncrementAsync("key-A", oldWindow);

        var freshCount = await limiter.IncrementAsync("key-A", oldWindow + Window.Ticks);

        Assert.Equal(1, freshCount);
    }
}