namespace SignalForge.Application.Security;

/// <summary>
/// Options governing outbound webhook deliveries (workflow steps and notification providers).
/// Defaults are deliberately strict: HTTPS only, private/loopback/link-local and cloud-metadata
/// destinations blocked, response bodies size-capped, and explicit timeouts applied to the
/// outbound HttpClient pipeline.
/// </summary>
public sealed class OutboundWebhookOptions
{
    public const int DefaultMaxResponseBytes = 1024 * 1024;
    public const int DefaultTimeoutSeconds = 30;
    public const int DefaultConnectTimeoutSeconds = 10;

    /// <summary>
    /// Hosts that may be reached over plain http. Keep empty in production; useful for local
    /// development against non-TLS test targets. Hosts are compared case-insensitively.
    /// </summary>
    public IReadOnlyList<string> AllowedHttpHosts { get; set; } = Array.Empty<string>();

    /// <summary>Maximum outbound response body size (default 1 MiB).</summary>
    public int MaxResponseBytes { get; set; } = DefaultMaxResponseBytes;

    /// <summary>Per-request total timeout for outbound deliveries.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    /// <summary>TCP connect timeout for outbound deliveries.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(DefaultConnectTimeoutSeconds);
}