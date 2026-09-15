using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SignalForge.Infrastructure.Data;

namespace SignalForge.IntegrationTests;

/// <summary>
/// End-to-end pin for the distributed rate-limit path: with <c>RateLimiting:Store=Sql</c> the real
/// middleware enforces the per-key budget through the shared SQL counter table, and the rejected
/// burst is visible in that table — proving the store, not a per-process bucket, is what throttled.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class SqlRateLimiterApiTests
{
    private const int PermitLimit = 3;

    private static readonly Guid TenantId = Guid.NewGuid();
    private const string TenantName = "itest-sql-ratelimit-tenant";
    private const string ApiKeyName = "itest-sql-ratelimit-key";
    private const string KeyA = "rlSqlK1-000000000000001";

    private readonly MsSqlContainerFixture _database;
    private readonly SqlStoreFactory _factory;

    public SqlRateLimiterApiTests(MsSqlContainerFixture database)
    {
        _database = database;
        _factory = new SqlStoreFactory(database);
    }

    [Fact]
    public async Task Burst_Beyond_Budget_Is_Rejected_429_And_Counted_In_SQL_Store()
    {
        var client = _factory.CreateClient(KeyA);

        for (var i = 0; i < PermitLimit; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/workflows")).StatusCode);

        var rejected = await client.GetAsync("/api/workflows");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // The counter row lives in the shared table: the throttle was driven by the SQL store.
        using var ctx = new SignalForgeDbContext(
            new DbContextOptionsBuilder<SignalForgeDbContext>()
                .UseSqlServer(_database.ConnectionString)
                .Options);
        var apiKeyId = await ctx.ApiKeys
            .Where(k => k.TenantId == TenantId)
            .Select(k => k.Id)
            .SingleOrDefaultAsync();
        Assert.NotEqual(Guid.Empty, apiKeyId);

        var windowSeconds = 60;
        var now = DateTimeOffset.UtcNow;
        var current = Application.RateLimiting.RateLimitWindow.GetWindowKey(
            now, TimeSpan.FromSeconds(windowSeconds));
        var previous = current - TimeSpan.FromSeconds(windowSeconds).Ticks;
        var apiKeyPrefix = apiKeyId.ToString();

        // Requests racing a 60s window boundary can land in either bucket; count both.
        var counts = await ctx.RateLimitCounters
            .Where(r => r.PartitionKey == apiKeyPrefix
                        && (r.WindowKey == current || r.WindowKey == previous))
            .Select(r => r.Count)
            .ToListAsync();

        Assert.True(counts.Sum() >= PermitLimit + 1,
            $"expected ≥{PermitLimit + 1} across windows, got {counts.Sum()}");
    }

    /// <summary>Hosts the real API with the SQL counter store and a small fixed-window budget.</summary>
    private sealed class SqlStoreFactory : WebApplicationFactory<Program>
    {
        private readonly MsSqlContainerFixture _database;

        public SqlStoreFactory(MsSqlContainerFixture database)
        {
            _database = database;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SignalForgeConnection"] = _database.ConnectionString,
                    ["RateLimiting:PermitLimit"] = PermitLimit.ToString(),
                    ["RateLimiting:WindowSeconds"] = "60",
                    ["RateLimiting:Store"] = "Sql",
                    ["Seed:Enabled"] = "true",
                    ["Seed:TenantId"] = TenantId.ToString(),
                    ["Seed:TenantName"] = TenantName,
                    ["Seed:ApiKeyName"] = ApiKeyName,
                    ["Seed:DefaultApiKey"] = KeyA
                });
            });
        }

        public HttpClient CreateClient(string apiKey)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization = null;
            client.DefaultRequestHeaders.Add(ApiTestFactory.ApiKeyHeader, apiKey);
            return client;
        }
    }
}