namespace SignalForge.Api.Observability;

/// <summary>
/// Configuration for the Prometheus scrape endpoint and backlog gauge refresh:
/// <c>Metrics:Enabled</c> turns the endpoint on/off, <c>Metrics:RefreshSeconds</c> paces the
/// database-backed gauge updates served on <c>/metrics</c>.
/// </summary>
public class MetricsOptions
{
    public const string SectionName = "Metrics";

    public bool Enabled { get; set; } = true;

    public int RefreshSeconds { get; set; } = 15;
}