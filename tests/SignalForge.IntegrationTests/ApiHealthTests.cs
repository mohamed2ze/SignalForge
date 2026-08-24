using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SignalForge.IntegrationTests;

/// <summary>
/// Covers the Stage 4 health endpoints: /health/live must answer 200 whenever the API process
/// is up (even if the DB is down), and /health/ready must answer 200 with the DB reachable and
/// 503 when it is not. Uses the shared Testcontainers SQL Server via the MsSqlCollection.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class ApiHealthTests
{
    private readonly MsSqlContainerFixture _database;

    public ApiHealthTests(MsSqlContainerFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task Live_endpoint_returns_200_when_db_is_available()
    {
        using var factory = new ApiTestFactory(_database);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_endpoint_returns_200_when_db_is_available()
    {
        using var factory = new ApiTestFactory(_database);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Live_endpoint_still_returns_200_when_db_is_down()
    {
        // A liveness probe must not depend on the database.
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    // Point at an unused port so any connection attempt is refused instantly.
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:SignalForgeConnection"] =
                            "Server=127.0.0.1,59999;Database=SignalForge;User Id=sa;Password=doesNotMatter_1;TrustServerCertificate=True",
                        ["Seed:Enabled"] = "false"
                    });
                });
            });
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_endpoint_returns_503_when_db_is_unreachable()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:SignalForgeConnection"] =
                            "Server=127.0.0.1,59999;Database=SignalForge;User Id=sa;Password=doesNotMatter_1;TrustServerCertificate=True",
                        ["Seed:Enabled"] = "false"
                    });
                });
            });
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}