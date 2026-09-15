namespace SignalForge.Application.Notifications;

/// <summary>
/// Configuration for the HTTP SMS gateway transport (config section: "Notifications:Sms"). The
/// transport POSTs an RFC-compatible JSON body (<c>{"to": ..., "message": ...}</c>) carrying the
/// API key header to <see cref="ApiBaseUrl"/>. Fails closed when <see cref="ApiBaseUrl"/> is empty.
/// </summary>
public class SmsNotificationOptions
{
    /// <summary>Gateway base URL the JSON body is POSTed to. Empty value means "not configured".</summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>Header name carrying the gateway credential (default <c>X-Api-Key</c>).</summary>
    public string ApiKeyHeader { get; set; } = "X-Api-Key";

    /// <summary>The gateway credential. Never logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Optional sender id/number included in the request body.</summary>
    public string? FromPhone { get; set; }

    /// <summary>Request timeout before the transport fails.</summary>
    public int TimeoutSeconds { get; set; } = 15;
}