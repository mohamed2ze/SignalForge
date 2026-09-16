using SignalForge.Domain.ValueObjects;

namespace SignalForge.Application.Services;

public interface IApiKeyValidationService
{
    Task<ApiKeyValidationResult> ValidateApiKeyAsync(string apiKey);
}

public class ApiKeyValidationResult
{
    public bool IsValid { get; init; }
    public TenantId? TenantId { get; init; }
    public ApiKeyId? ApiKeyId { get; init; }
    public string? ApiKeyName { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>The tenant's webhook signing secret, resolved in the same query as the key so the
    /// events pipeline never needs a second round-trip to sign/verify. Secret — never log it.</summary>
    public string? TenantWebhookSigningSecret { get; init; }
}