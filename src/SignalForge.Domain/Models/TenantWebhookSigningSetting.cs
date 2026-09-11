namespace SignalForge.Domain.Models;

/// <summary>
/// Per-tenant signing secret used to verify inbound webhook event signatures
/// (HMAC-SHA256 over the raw request body). Unlike API keys, the secret must be available in
/// recoverable form at request time to recompute the expected signature, so it is stored
/// plaintext server-side; values are high-entropy and must never be logged or echoed.
/// </summary>
public class TenantWebhookSigningSetting
{
    public Guid TenantId { get; private set; }
    public string SigningSecret { get; private set; } = default!;
    public DateTime UpdatedAt { get; private set; }

    private TenantWebhookSigningSetting() { } // For EF Core

    private TenantWebhookSigningSetting(Guid tenantId, string signingSecret)
    {
        TenantId = tenantId;
        SigningSecret = signingSecret;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a signing setting for a tenant.
    /// </summary>
    public static TenantWebhookSigningSetting Create(Guid tenantId, string signingSecret)
    {
        if (string.IsNullOrWhiteSpace(signingSecret))
            throw new ArgumentException("Signing secret cannot be empty", nameof(signingSecret));

        return new TenantWebhookSigningSetting(tenantId, signingSecret);
    }

    /// <summary>
    /// Rotates the signing secret.
    /// </summary>
    public void Rotate(string signingSecret)
    {
        if (string.IsNullOrWhiteSpace(signingSecret))
            throw new ArgumentException("Signing secret cannot be empty", nameof(signingSecret));

        SigningSecret = signingSecret;
        UpdatedAt = DateTime.UtcNow;
    }
}