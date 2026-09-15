using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SignalForge.IntegrationTests;

/// <summary>
/// Verifies the Prometheus scrape endpoint: <c>/metrics</c> returns text/plain with the app's
/// gauges and the database-backed backlog graphs, and the events-ingested counter advances by
/// exactly one per accepted POST /api/events. The counter family only publishes once its first
/// observation exists (prometheus-net lazy registration), so counters are read as zero when absent
/// and asserted by before/after delta — never by absolute value — because the process-global
/// registry is shared across the whole test run.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class MetricsEndpointTests
{
    private static readonly Regex CounterLine = new(
        @"^\s*(signalforge_[a-z_]+)\s+(\d+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly MsSqlContainerFixture _database;
    private readonly ApiTestFactory _factory;

    public MetricsEndpointTests(MsSqlContainerFixture database)
    {
        _database = database;
        _factory = new ApiTestFactory(database);
    }

    [Fact]
    public async Task Metrics_Endpoint_Exposes_Http_Metrics_And_Backlog_Gauges()
    {
        var body = await _factory.CreateClient().GetStringAsync("/metrics");

        // Built-in HTTP + .NET runtime metrics, always published.
        Assert.Contains("http_requests_received_total", body);
        Assert.Contains("http_request_duration_seconds", body);
        Assert.Contains("dotnet_total_memory_bytes", body);

        // DB-backed backlog gauges, refreshed by the hosted service regardless of traffic.
        Assert.Contains("signalforge_outbox_pending", body);
        Assert.Contains("signalforge_executions_active", body);
        Assert.Contains("signalforge_deadletter_backlog", body);
    }

    [Fact]
    public async Task Metrics_Endpoint_Responds_With_Prometheus_Content_Type()
    {
        var response = await _factory.CreateClient().GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/plain", response.Content.Headers.ContentType!.MediaType!);
    }

    [Fact]
    public async Task Events_Ingested_Counter_Advances_By_One_Per_Accepted_Event()
    {
        var client = _factory.CreateClientForSeededTenant();
        var externalId = $"metrics-ext-{Guid.NewGuid():N}";

        var before = await ReadCounterAsync("signalforge_events_ingested_total");

        var raw = EventSigner.CamelJson(new
        {
            ExternalEventId = externalId,
            EventType = "order.created",
            Payload = "{\"orderId\":99}"
        });
        var response = await EventSigner.PostSignedEventAsync(
            client, ApiTestFactory.SigningSecret, raw);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var after = await ReadCounterAsync("signalforge_events_ingested_total");

        Assert.Equal(before + 1, after);
    }

    /// <summary>
    /// Reads a counter's current value from /metrics. The value line is the bare
    /// <c>name number</c> line (the <c># HELP</c>/<c># TYPE</c> lines start with '#'), and a family
    /// that has never been incremented does not publish at all — treated as zero.
    /// </summary>
    private async Task<long> ReadCounterAsync(string metricName)
    {
        var body = await _factory.CreateClient().GetStringAsync("/metrics");
        return CounterLine.Matches(body)
            .Where(m => m.Groups[1].Value == metricName)
            .Select(m => long.Parse(m.Groups[2].Value))
            .SingleOrDefault();
    }
}