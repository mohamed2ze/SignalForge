using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SignalForge.Application.Data;
using SignalForge.Domain.ValueObjects;

namespace SignalForge.Application.Services;

/// <summary>
/// Implementation of API key validation service.
/// </summary>
public class ApiKeyValidationService : IApiKeyValidationService
{
    private readonly ISignalForgeDbContext _dbContext;
    private readonly ILogger<ApiKeyValidationService>? _logger;

    public ApiKeyValidationService(ISignalForgeDbContext dbContext, ILogger<ApiKeyValidationService>? logger = null)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ApiKeyValidationResult> ValidateApiKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ApiKeyValidationResult
            {
                IsValid = false,
                ErrorMessage = "API key is required"
            };
        }

        // Extract prefix (first 8 characters) for lookup
        var keyPrefix = apiKey.Length >= 8 ? apiKey.Substring(0, 8) : apiKey;

        // Find API key by prefix
        var apiKeyEntity = await _dbContext.ApiKeys
            .Include(ak => ak.Tenant)
            .FirstOrDefaultAsync(ak => ak.KeyPrefix == keyPrefix && ak.IsActive);

        if (apiKeyEntity == null)
        {
            return new ApiKeyValidationResult
            {
                IsValid = false,
                ErrorMessage = "Invalid API key"
            };
        }

        // Deactivated tenants never validate, even with a structurally correct key. Without the
        // global Tenant soft-delete filter (Decision #28) the Include above would surface the
        // tenant either way, so the deactivation check is explicit here.
        if (apiKeyEntity.Tenant is { DeletedAt: not null })
        {
            return new ApiKeyValidationResult
            {
                IsValid = false,
                ErrorMessage = "Invalid API key"
            };
        }

        // Verify the full key hash
        // Delegates to the domain method, which is the single source of truth for key
        // verification (SHA-256 + base64, constant-time compare), consistent with ApiKey.Create.
        if (!apiKeyEntity.VerifyKey(apiKey))
        {
            return new ApiKeyValidationResult
            {
                IsValid = false,
                ErrorMessage = "Invalid API key"
            };
        }

        // Mark the key as used. LastUsedAt is a best-effort observation, not a gate: it shares a
        // concurrency token (UpdatedAt) with other updates, so under concurrent validation the
        // write can lose the race and throw DbUpdateConcurrencyException. The key is still
        // verified valid — losing the usage stamp must not fail the request (Decision #26).
        apiKeyEntity.MarkAsUsed();
        try
        {
            await _dbContext.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger?.LogDebug(ex,
                "Api key {ApiKeyId} for tenant {TenantId} validated, but the usage stamp was " +
                "dropped by a concurrent update; validation still succeeds",
                apiKeyEntity.Id, apiKeyEntity.TenantId);
        }

        return new ApiKeyValidationResult
        {
            IsValid = true,
            TenantId = new TenantId(apiKeyEntity.TenantId),
            ApiKeyId = new ApiKeyId(apiKeyEntity.Id),
            ApiKeyName = apiKeyEntity.Name
        };
    }
}