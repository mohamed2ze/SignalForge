using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SignalForge.IntegrationTests;

/// <summary>
/// Proves the Stage 4 structured-logging enrichment: every request's log line must carry the
/// stable scoped property names correlationId / tenantId / apiKeyId (the JSON formatter emits
/// these verbatim for containers). A captured logger provider inspects the scope stack rather
/// than the formatter, so this exercises the middleware enrichment independent of JSON output.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class StructuredLoggingTests
{
    private readonly MsSqlContainerFixture _database;

    public StructuredLoggingTests(MsSqlContainerFixture database)
    {
        _database = database;
    }

    private sealed class CapturedMessage
    {
        public required string Category { get; init; }
        public required LogLevel Level { get; init; }
        public required string Message { get; init; }
        public required IReadOnlyList<KeyValuePair<string, object?>> ScopeProperties { get; init; }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly LoggerExternalScopeProvider _scopes = new();
        public ConcurrentBag<CapturedMessage> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) =>
            new CapturingLogger(categoryName, Messages, _scopes);

        public void Dispose() { }

        private sealed class CapturingLogger(
            string category,
            ConcurrentBag<CapturedMessage> messages,
            LoggerExternalScopeProvider scopes) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var scopeProps = new List<KeyValuePair<string, object?>>();
                scopes.ForEachScope((scopeState, list) =>
                {
                    if (scopeState is IEnumerable<KeyValuePair<string, object?>> kvPairs)
                        list.AddRange(kvPairs);
                }, scopeProps);

                messages.Add(new CapturedMessage
                {
                    Category = category,
                    Level = logLevel,
                    Message = formatter(state, exception),
                    ScopeProperties = scopeProps
                });
            }
        }
    }

    [Fact]
    public async Task Request_logs_carry_correlation_tenant_and_apiKey_scope_properties()
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
                        ["Seed:TenantId"] = ApiTestFactory.TenantId.ToString(),
                        ["Seed:TenantName"] = ApiTestFactory.TenantName,
                        ["Seed:ApiKeyName"] = ApiTestFactory.ApiKeyName,
                        ["Seed:DefaultApiKey"] = ApiTestFactory.ApiKey,
                        ["Seed:SigningSecret"] = ApiTestFactory.SigningSecret
                    });
                });
                builder.ConfigureLogging(logging => logging.AddProvider(captured));
            });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiTestFactory.ApiKeyHeader, ApiTestFactory.ApiKey);

        var response = await client.GetAsync("/api/workflows");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        // The request-scope middleware adds a log line after the inner handler completes.
        var requestLog = captured.Messages.FirstOrDefault(
            m => m.Category == "SignalForge.Api.Middleware.LoggingScopeMiddleware");
        Assert.NotNull(requestLog);

        var props = requestLog!.ScopeProperties
            .GroupBy(p => p.Key)
            .ToDictionary(g => g.Key, g => g.Last().Value);

        var correlationId = Assert.IsType<string>(props.GetValueOrDefault("correlationId"));
        Assert.False(string.IsNullOrEmpty(correlationId));

        var tenantId = Assert.IsType<string>(props.GetValueOrDefault("tenantId"));
        Assert.Equal(ApiTestFactory.TenantId.ToString(), tenantId);

        var apiKeyId = Assert.IsType<string>(props.GetValueOrDefault("apiKeyId"));
        Assert.False(string.IsNullOrEmpty(apiKeyId));
    }
}