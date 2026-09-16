using SignalForge.Domain.Security;

namespace SignalForge.Domain.Models;

public class ApiKey
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string KeyHash { get; private set; } = default!;
    public string? KeyPrefix { get; private set; } // First few chars for identification (not secret)
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? ExpiresAt { get; private set; }
    public DateTime? LastUsedAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; } // For concurrency tracking

    // Navigation properties
    public Tenant Tenant { get; private set; } = default!;

    private ApiKey() { } // For EF Core

    private ApiKey(Guid id, Guid tenantId, string name, string keyHash, string? keyPrefix = null)
    {
        Id = id;
        TenantId = tenantId;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        KeyHash = keyHash ?? throw new ArgumentNullException(nameof(keyHash));
        KeyPrefix = keyPrefix;
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public static ApiKey Create(Guid tenantId, string name, string plainTextKey)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("API key name cannot be empty", nameof(name));

        if (string.IsNullOrWhiteSpace(plainTextKey))
            throw new ArgumentException("API key cannot be empty", nameof(plainTextKey));

        // Hash the API key for storage with a salted KDF (versioned PBKDF2), never bare SHA-256.
        var keyHash = ApiKeyHasher.Hash(plainTextKey);

        // Extract prefix for identification (first 8 characters)
        var keyPrefix = plainTextKey.Length >= 8 ? plainTextKey.Substring(0, 8) : plainTextKey;

        return new ApiKey(
            Guid.NewGuid(),
            tenantId,
            name.Trim(),
            keyHash,
            keyPrefix);
    }

    /// <summary>
    /// Verifies if the provided plain-text key matches the stored hash. Supports both the current
    /// versioned PBKDF2 format and legacy bare SHA-256 hashes.
    /// </summary>
    /// <param name="plainTextKey">The plain-text API key to verify</param>
    /// <returns>True if the key matches, false otherwise</returns>
    public bool VerifyKey(string plainTextKey)
        => ApiKeyHasher.Verify(plainTextKey, KeyHash);

    /// <summary>True when the stored hash predates the salted-format rollout and should be
    /// re-hashed after a successful verification.</summary>
    public bool RequiresKeyHashMigration => !ApiKeyHasher.IsVersionedHash(KeyHash);

    /// <summary>
    /// Re-hashes the key with the current salted format after a successful legacy verification so
    /// the upgrade is transparent to the tenant.
    /// </summary>
    public void RehashKey(string plainTextKey)
    {
        if (!RequiresKeyHashMigration)
            throw new InvalidOperationException("Key hash already uses the current format");

        KeyHash = ApiKeyHasher.Hash(plainTextKey);
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateInfo(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("API key name cannot be empty", nameof(name));

        Name = name.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Activate()
    {
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Revoke()
    {
        IsActive = false;
        RevokedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkAsUsed()
    {
        LastUsedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public bool IsExpired()
    {
        return ExpiresAt.HasValue && ExpiresAt.Value < DateTime.UtcNow;
    }

    public bool IsValid()
    {
        return IsActive && !IsExpired() && RevokedAt == null;
    }
}