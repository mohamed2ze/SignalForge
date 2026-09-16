using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.IntegrationTests;

/// <summary>
/// Regression pins for M1 (rate limiting): the API carries a built-in fixed-window limiter keyed
/// by the authenticated API-key id (per-key budget), with health probes exempted. A dedicated
/// host applies a low 3 req/min budget so the burst is deterministic without touching the
/// default-limit factory used by the rest of the suite (whose tenants must never see 429s).
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class RateLimitingTests
{
    private const int PermitLimit = 3;

    private static readonly Guid TenantId = Guid.NewGuid();
    private const string TenantName = "itest-rate-limit-tenant";
    private const string ApiKeyName = "itest-rate-limit-key";
    private const string KeyA = "rlTestK1-000000000000001";
    private const string KeyB = "rlTestK2-000000000000002";

    private readonly MsSqlContainerFixture _database;
    private readonly LowLimitFactory _factory;

    public RateLimitingTests(MsSqlContainerFixture database)
    {
        _database = database;
        // The container is shared, but each factory instance has its own in-memory limiter, so the
        // dedicated low-limit factory is isolated from the default-limit one.
        _factory = new LowLimitFactory(database);
    }

    [Fact]
    public async Task Burst_Beyond_Budget_Is_Rejected_With_429()
    {
        using var scope = _factory.Services.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        Assert.Equal(PermitLimit.ToString(),
            config["RateLimiting:PermitLimit"] ?? throw new InvalidOperationException("missing config"));

        var client = _factory.CreateClient(KeyA);

        for (var i = 0; i < PermitLimit; i++)
        {
            var withinBudget = await client.GetAsync("/api/workflows");
            Assert.Equal(HttpStatusCode.OK, withinBudget.StatusCode);
        }

        var rejected = await client.GetAsync("/api/workflows");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Other_ApiKey_Keeps_Its_Own_Budget()
    {
        await SeedSecondApiKeyAsync();

        var keyClient = _factory.CreateClient(KeyA);
        var otherClient = _factory.CreateClient(KeyB);

        // Exhaust the first key's budget.
        for (var i = 0; i < PermitLimit; i++)
            Assert.Equal(HttpStatusCode.OK, (await keyClient.GetAsync("/api/workflows")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await keyClient.GetAsync("/api/workflows")).StatusCode);

        // A different API key has its own untouched bucket.
        Assert.Equal(HttpStatusCode.OK, (await otherClient.GetAsync("/api/workflows")).StatusCode);
    }

    [Fact]
    public async Task Health_Probes_Are_Not_Rate_Limited()
    {
        var client = _factory.CreateClient(KeyA);

        for (var i = 0; i < PermitLimit; i++)
            await client.GetAsync("/api/workflows");

        // Readiness stays reachable even while the caller's API budget is exhausted.
        var health = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    /// <summary>Idempotently adds a second API key for the factory's seeded tenant.</summary>
    private async Task SeedSecondApiKeyAsync()
    {
        using var ctx = new SignalForgeDbContext(
            new DbContextOptionsBuilder<SignalForgeDbContext>()
                .UseSqlServer(_database.ConnectionString)
                .Options);

        var otherPrefix = KeyB[..8];
        if (!await ctx.ApiKeys.AnyAsync(k => k.TenantId == TenantId && k.KeyPrefix == otherPrefix))
            ctx.ApiKeys.Add(ApiKey.Create(TenantId, "other-key", KeyB));

        await ctx.SaveChangesAsync();
    }

    private sealed class LowLimitFactory : WebApplicationFactory<Program>
    {
        private readonly MsSqlContainerFixture _database;

        public LowLimitFactory(MsSqlContainerFixture database)
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