using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SignalForge.IntegrationTests;

/// <summary>
/// H3: secrets hygiene. The webhook signing secret and the API key must never appear in the log
/// output — neither on startup seeding (the opt-in <c>ExposeGeneratedSecrets</c> path is off) nor
/// while the authentication/signing middleware processes real requests that carry those values.
/// A capturing logger provider watches every category emitted by the host.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class SecretsHygieneTests
{
    private const string DistinctSecret = "SFH3-SECRET-7f2c9e1a-0000-0000-0000-deadbeef0001";
    private const string DistinctApiKey = "SFH3-KEY-0000-0000-0000-000000000001";
    private const string DistinctTenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0001";
    private const string DistinctKeyName = "itest-secret-key";

    private readonly MsSqlContainerFixture _database;

    public SecretsHygieneTests(MsSqlContainerFixture database)
    {
        _database = database;
    }

    private sealed class LogLine
    {
        public required string Category { get; init; }
        public required string Message { get; init; }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<LogLine> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Lines);

        public void Dispose() { }

        private sealed class CapturingLogger(
            string category,
            ConcurrentBag<LogLine> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => lines.Add(new LogLine { Category = category, Message = formatter(state, exception) });
        }
    }

    [Fact]
    public async Task Signing_secret_and_api_key_never_appear_in_log_output()
    {
        var captured = new CapturingLoggerProvider();

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:SignalForgeConnection"] = _database.ConnectionString,
                        ["Seed:Enabled"] = "true",
                        ["Seed:TenantId"] = DistinctTenantId,
                        ["Seed:TenantName"] = "Secrets Hygiene Tenant",
                        ["Seed:ApiKeyName"] = DistinctKeyName,
                        ["Seed:DefaultApiKey"] = DistinctApiKey,
                        ["Seed:SigningSecret"] = DistinctSecret,
                        ["Seed:ExposeGeneratedSecrets"] = "false"
                    });
                });
                builder.ConfigureLogging(logging => logging.AddProvider(captured));
            });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiTestFactory.ApiKeyHeader, DistinctApiKey);

        // Exercise authentication, request-scoped logging, and signing-secret loading.
        var workflows = await client.GetAsync("/api/workflows");
        Assert.Equal(System.Net.HttpStatusCode.OK, workflows.StatusCode);

        // Exercise the HMAC signing path, which reads the secret from the DB-backed setting.
        var rawEvent = EventSigner.CamelJson(new
        {
            ExternalEventId = $"h3-{Guid.NewGuid():N}",
            EventType = "order.created",
            Payload = "{}"
        });
        var signed = await EventSigner.PostSignedEventAsync(client, DistinctSecret, rawEvent);
        Assert.True(
            signed.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.Created,
            $"expected 200/201, got {(int)signed.StatusCode}");

        // Force a signature-mismatch path too (the middleware logs a warning for the request).
        var badContent = EventSigner.SignedContent("wrong-secret-used-for-signature", rawEvent);
        var rejected = await client.PostAsync("/api/events", badContent);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);

        var lines = captured.Lines.ToArray();
        Assert.NotEmpty(lines);
        Assert.DoesNotContain(lines,
            l => l.Message.Contains(DistinctSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(lines,
            l => l.Message.Contains(DistinctApiKey, StringComparison.Ordinal));
    }
}