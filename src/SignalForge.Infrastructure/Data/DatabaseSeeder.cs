using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SignalForge.Domain.Models;

namespace SignalForge.Infrastructure.Data;

/// <summary>
/// Idempotent database seeder.
/// Ensures the platform has a default tenant, a sample local-development API key, and a
/// per-tenant webhook signing secret on first run. All actions are idempotent: calling multiple
/// times never duplicates data.
/// </summary>
public static class DatabaseSeeder
{
    public static readonly Guid DefaultTenantId = new("3fa85f64-5717-4562-b3fc-2c963f66afa6");
    private const string DefaultTenantName = "Default";
    private const string DefaultApiKeyName = "Local Development";
    private const int SampleKeyLength = 32;
    private const int SampleSigningSecretLength = 48;

    public sealed record SeedResult(string? ApiKey, string? SigningSecret);

    public static async Task<SeedResult> EnsureSeedDataAsync(
        SignalForgeDbContext dbContext,
        Guid? tenantIdOverride = null,
        string? tenantNameOverride = null,
        string? apiKeyNameOverride = null,
        string? plainTextApiKey = null,
        string? signingSecretOverride = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantIdOverride ?? DefaultTenantId;
        var tenantName = tenantNameOverride ?? DefaultTenantName;

        // 1. Default tenant
        var tenantExists = await dbContext.Tenants
            .AnyAsync(t => t.Id == tenantId, cancellationToken);

        if (!tenantExists)
        {
            dbContext.Tenants.Add(Tenant.CreateWithId(tenantId, tenantName));
        }

        // 2. Sample API key (only if no key exists for this tenant)
        var seededApiKey = (string?)null;
        var hasAnyKey = await dbContext.ApiKeys
            .AnyAsync(ak => ak.TenantId == tenantId, cancellationToken);

        if (!hasAnyKey)
        {
            var effectivePlainTextKey = plainTextApiKey ?? GenerateSecureSampleKey();
            var keyName = apiKeyNameOverride ?? DefaultApiKeyName;

            dbContext.ApiKeys.Add(ApiKey.Create(tenantId, keyName, effectivePlainTextKey));
            seededApiKey = effectivePlainTextKey;
        }

        // 3. Per-tenant webhook signing secret (only if none exists for this tenant). Unlike the
        // API key it is stored in recoverable form — recomputing HMAC at request time needs the
        // plaintext — so it must never be logged or echoed.
        var seededSigningSecret = (string?)null;
        var hasSigningSecret = await dbContext.TenantWebhookSigningSettings
            .AnyAsync(s => s.TenantId == tenantId, cancellationToken);

        if (!hasSigningSecret)
        {
            var secret = signingSecretOverride ?? GenerateSecureSigningSecret();
            dbContext.TenantWebhookSigningSettings.Add(
                TenantWebhookSigningSetting.Create(tenantId, secret));
            seededSigningSecret = secret;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return new SeedResult(seededApiKey, seededSigningSecret);
    }

    // Sample dev credentials are generated with a cryptographic random number generator:
    // System.Random is time-seeded and predictable, so a generated API key or signing secret
    // could otherwise be brute-forced if it leaked (e.g. via a log).

    private static string GenerateSecureSampleKey()
    {
        return GenerateRandomToken(SampleKeyLength);
    }

    private static string GenerateSecureSigningSecret()
    {
        return GenerateRandomToken(SampleSigningSecretLength);
    }

    private static string GenerateRandomToken(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var buffer = new char[length];
        for (var i = 0; i < buffer.Length; i++)
        {
            // GetInt32 is rejection-sampling on the CSPRNG: unbiased across the full charset,
            // unlike the previous mod-62 reduction over a 256-alphabet byte.
            buffer[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        }
        return new string(buffer);
    }
}