using SignalForge.Domain.ValueObjects;

namespace SignalForge.Application.Services;

/// <summary>
/// Service for validating API keys.
/// </summary>
public interface IApiKeyValidationService
{
    /// <summary>
    /// Validates the provided API key and returns the associated tenant and API key information.
    /// </summary>
    /// <param name="apiKey">The API key to validate.</param>
    /// <returns>Validation result containing tenant ID, API key ID, and API key name if valid.</returns>
    Task<ApiKeyValidationResult> ValidateApiKeyAsync(string apiKey);
}

/// <summary>
/// Result of API key validation.
/// </summary>
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