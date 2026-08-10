using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Data;
using SignalForge.Domain.ValueObjects;

namespace SignalForge.Application.Services;

/// <summary>
/// Implementation of API key validation service.
/// </summary>
public class ApiKeyValidationService : IApiKeyValidationService
{
    private readonly ISignalForgeDbContext _dbContext;

    public ApiKeyValidationService(ISignalForgeDbContext dbContext)
    {
        _dbContext = dbContext;
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

        // Mark the key as used
        apiKeyEntity.MarkAsUsed();
        await _dbContext.SaveChangesAsync();

        return new ApiKeyValidationResult
        {
            IsValid = true,
            TenantId = new TenantId(apiKeyEntity.TenantId),
            ApiKeyId = new ApiKeyId(apiKeyEntity.Id),
            ApiKeyName = apiKeyEntity.Name
        };
    }
}