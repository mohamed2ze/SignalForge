using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SignalForge.IntegrationTests;

/// <summary>
/// Hosts the real API (Program.cs) in-memory against the Testcontainers SQL Server.
/// The connection string and seed overrides are injected via configuration so the API's
/// startup seeder provisions a deterministic tenant + API key that the HTTP tests rely on.
/// </summary>
public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    public const string ApiKeyHeader = "X-API-Key";

    public static readonly Guid TenantId = new("11111111-2222-3333-4444-555555555555");
    public const string TenantName = "itest-seeded-tenant";
    public const string ApiKeyName = "itest-seeded-key";
    public const string ApiKey = "sfTestK1-seeded-key-0001";
    public const string SigningSecret = "sfTestK2-seeded-signing-secret-0001";

    private readonly MsSqlContainerFixture _database;

    public ApiTestFactory(MsSqlContainerFixture database)
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
                ["Seed:Enabled"] = "true",
                ["Seed:TenantId"] = TenantId.ToString(),
                ["Seed:TenantName"] = TenantName,
                ["Seed:ApiKeyName"] = ApiKeyName,
                ["Seed:DefaultApiKey"] = ApiKey,
                ["Seed:SigningSecret"] = SigningSecret
            });
        });
    }

    /// <summary>
    /// A pre-authenticated Http client carrying the given API key header.
    /// </summary>
    public HttpClient CreateClient(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Add(ApiKeyHeader, apiKey);
        return client;
    }

    /// <summary>
    /// A pre-authenticated Http client carrying the seeded default-tenant API key.
    /// </summary>
    public HttpClient CreateClientForSeededTenant() => CreateClient(ApiKey);
}