using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SignalForge.Application.Data;
using SignalForge.Application.RateLimiting;
using SignalForge.Infrastructure.Data;
using SignalForge.Infrastructure.RateLimiting;

namespace SignalForge.IntegrationTests;

/// <summary>
/// Cross-node pins for the SQL rate-limit store (<c>RateLimiting:Store=Sql</c>): any number of API
/// instances deriving window keys from wall-clock time and incrementing the same table rows share a
/// single budget. Two independent <see cref="SqlRateLimiter"/> instances therefore observe each
/// other's counts — the behaviour the in-process limiter cannot provide.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class SqlRateLimiterTests
{
    private readonly MsSqlContainerFixture _database;

    public SqlRateLimiterTests(MsSqlContainerFixture database)
    {
        _database = database;
    }

    private IServiceProvider BuildServices() => new ServiceCollection()
        .AddDbContext<SignalForgeDbContext>(options =>
            options.UseSqlServer(_database.ConnectionString))
        .AddScoped<ISignalForgeDbContext>(sp => sp.GetRequiredService<SignalForgeDbContext>())
        .BuildServiceProvider();

    private static async Task<long> IncrementAsync(
        IServiceProvider services,
        string key,
        long windowKey) =>
        await new SqlRateLimiter(services).IncrementAsync(key, windowKey);

    private static async Task<long> GetCountAsync(
        IServiceProvider services,
        string key,
        long windowKey) =>
        await new SqlRateLimiter(services).GetCountAsync(key, windowKey);

    [Fact]
    public async Task Two_Instances_Share_One_Budget_Across_Nodes()
    {
        var nodeA = BuildServices();
        var nodeB = BuildServices();
        var windowKey = RateLimitWindow.GetWindowKey(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

        // Node A takes two permits, Node B one — all three land in the same shared count.
        Assert.Equal(1, await IncrementAsync(nodeA, "shared-key", windowKey));
        Assert.Equal(2, await IncrementAsync(nodeA, "shared-key", windowKey));
        Assert.Equal(3, await IncrementAsync(nodeB, "shared-key", windowKey));

        // And either node sees the aggregate.
        Assert.Equal(3, await GetCountAsync(nodeA, "shared-key", windowKey));
        Assert.Equal(3, await GetCountAsync(nodeB, "shared-key", windowKey));
    }

    [Fact]
    public async Task Concurrent_First_Window_Burst_Counts_Every_Request_Once()
    {
        var services = BuildServices();
        var windowKey = RateLimitWindow.GetWindowKey(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

        // A common first-request-in-window race: several nodes all try to create the row at once.
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => IncrementAsync(services, "race-key", windowKey))
            .ToArray();

        var counts = await Task.WhenAll(tasks);

        Assert.All(counts, c => Assert.True(c >= 1));
        Assert.Equal(50, await GetCountAsync(services, "race-key", windowKey));
    }

    [Fact]
    public async Task New_Window_Starts_A_Fresh_Budget()
    {
        var services = BuildServices();
        var windowA = RateLimitWindow.GetWindowKey(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        var windowB = windowA + 60_000_000; // +1 minute, a fresh bucket

        await IncrementAsync(services, "roll-key", windowA);
        await IncrementAsync(services, "roll-key", windowA);

        Assert.Equal(1, await IncrementAsync(services, "roll-key", windowB));
    }
}