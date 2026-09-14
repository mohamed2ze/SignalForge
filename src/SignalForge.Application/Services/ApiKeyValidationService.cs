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

        // Find API key by prefix, resolving the tenant and the tenant's webhook signing secret in the
        // SAME round-trip (projection, not Include: TenantWebhookSigningSetting deliberately has no
        // FK/nav to Tenant). This is what lets the events pipeline skip its own DB query later.
        var matched = await (
                from ak in _dbContext.ApiKeys
                join t in _dbContext.Tenants on ak.TenantId equals t.Id into tenants
                from tenant in tenants.DefaultIfEmpty()
                join s in _dbContext.TenantWebhookSigningSettings on ak.TenantId equals s.TenantId into signing
                from setting in signing.DefaultIfEmpty()
                where ak.KeyPrefix == keyPrefix && ak.IsActive
                select new { ApiKey = ak, Tenant = tenant, Setting = setting })
            .FirstOrDefaultAsync();

        if (matched is null || matched.ApiKey is null)
        {
            return new ApiKeyValidationResult
            {
                IsValid = false,
                ErrorMessage = "Invalid API key"
            };
        }

        // Deactivated tenants never validate, even with a structurally correct key. Soft delete is not
        // enforced by global query filters, so the join above would surface the tenant either way
        // and the deactivation check is explicit here.
        if (matched.Tenant is null || matched.Tenant.DeletedAt is not null)
        {
            return new ApiKeyValidationResult
            {
                IsValid = false,
                ErrorMessage = "Invalid API key"
            };
        }

        var apiKeyEntity = matched.ApiKey;

        // Verify the full key hash
        // Delegates to the domain method, which is the single source of truth for key
        // verification (versioned salted PBKDF2, constant-time compare), consistent with ApiKey.Create.
        if (!apiKeyEntity.VerifyKey(apiKey))
        {
            return new ApiKeyValidationResult
            {
                IsValid = false,
                ErrorMessage = "Invalid API key"
            };
        }

        // Transparently upgrade legacy unsalted SHA-256 hashes to the salted format on a
        // successful verification so the scheme converges without forcing tenant action.
        if (apiKeyEntity.RequiresKeyHashMigration)
            apiKeyEntity.RehashKey(apiKey);

        // Mark the key as used. LastUsedAt is a best-effort observation, not a gate: it shares a
        // concurrency token (UpdatedAt) with other updates, so under concurrent validation the
        // write can lose the race and throw DbUpdateConcurrencyException. The key is still
        // verified valid — losing the usage stamp must not fail the request.
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

            // A failed SaveChanges leaves the key Modified with STALE original values (including the
            // UpdatedAt concurrency token). A later SaveChanges in the same request scope — e.g. the
            // endpoint's own write — would re-attempt that UPDATE against the now-advanced token,
            // fail, and take the whole request down. Detach from the raced write so this scope only
            // persists what the endpoint itself changes.
            _dbContext.Entry(apiKeyEntity).State = EntityState.Unchanged;
        }

        return new ApiKeyValidationResult
        {
            IsValid = true,
            TenantId = new TenantId(apiKeyEntity.TenantId),
            ApiKeyId = new ApiKeyId(apiKeyEntity.Id),
            ApiKeyName = apiKeyEntity.Name,
            TenantWebhookSigningSecret = matched.Setting?.SigningSecret
        };
    }
}