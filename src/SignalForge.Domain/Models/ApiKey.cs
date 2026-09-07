using System.Security.Cryptography;
using System.Text;

namespace SignalForge.Domain.Models;

/// <summary>
/// Represents an API key for tenant authentication.
/// API keys are hashed before storage for security.
/// </summary>
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

    /// <summary>
    /// Creates a new API key with automatic hashing.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="name">The API key name/description</param>
    /// <param name="plainTextKey">The plain-text API key (will be hashed)</param>
    /// <returns>A new ApiKey instance</returns>
    public static ApiKey Create(Guid tenantId, string name, string plainTextKey)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("API key name cannot be empty", nameof(name));

        if (string.IsNullOrWhiteSpace(plainTextKey))
            throw new ArgumentException("API key cannot be empty", nameof(plainTextKey));

        // Hash the API key for storage
        var keyBytes = Encoding.UTF8.GetBytes(plainTextKey);
        var hashBytes = SHA256.HashData(keyBytes);
        var keyHash = Convert.ToBase64String(hashBytes);

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
    /// Verifies if the provided plain-text key matches the stored hash.
    /// </summary>
    /// <param name="plainTextKey">The plain-text API key to verify</param>
    /// <returns>True if the key matches, false otherwise</returns>
    public bool VerifyKey(string plainTextKey)
    {
        if (string.IsNullOrWhiteSpace(plainTextKey))
            return false;

        var keyBytes = Encoding.UTF8.GetBytes(plainTextKey);
        var hashBytes = SHA256.HashData(keyBytes);
        var computedHash = Convert.ToBase64String(hashBytes);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(KeyHash),
            Encoding.UTF8.GetBytes(computedHash));
    }

    /// <summary>
    /// Updates the API key information.
    /// </summary>
    /// <param name="name">The new API key name</param>
    public void UpdateInfo(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("API key name cannot be empty", nameof(name));

        Name = name.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Deactivates the API key.
    /// </summary>
    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Activates the API key.
    /// </summary>
    public void Activate()
    {
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Revokes the API key permanently.
    /// </summary>
    public void Revoke()
    {
        IsActive = false;
        RevokedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Marks the API key as used.
    /// </summary>
    public void MarkAsUsed()
    {
        LastUsedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Checks if the API key is expired.
    /// </summary>
    /// <returns>True if expired, false otherwise</returns>
    public bool IsExpired()
    {
        return ExpiresAt.HasValue && ExpiresAt.Value < DateTime.UtcNow;
    }

    /// <summary>
    /// Checks if the API key is valid for use.
    /// </returns>
    /// <returns>True if active, not expired, and not revoked</returns>
    public bool IsValid()
    {
        return IsActive && !IsExpired() && RevokedAt == null;
    }
}