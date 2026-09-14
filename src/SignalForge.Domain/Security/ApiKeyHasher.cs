using System.Security.Cryptography;
using System.Text;

namespace SignalForge.Domain.Security;

/// <summary>
/// Hashes API keys for storage. New keys use salted PBKDF2-SHA256 with a versioned, self-describing
/// format: <c>$pbkdf2-sha256$&lt;iterations&gt;$&lt;salt-b64&gt;$&lt;hash-b64&gt;</c>. Legacy bare
/// base64 SHA-256 hashes remain verifiable (and are transparently re-hashed on a successful login),
/// so upgrading the scheme never invalidates existing keys.
/// <para>
/// PBKDF2 is used instead of bcrypt/argon2 to avoid a new runtime dependency while still deriving
/// the key through a password KDF with a per-key salt.
/// </para>
/// </summary>
public static class ApiKeyHasher
{
    public const string HashVersionPrefix = "$pbkdf2-sha256$";

    private const int SaltSize = 16; // 128-bit salt
    private const int HashSize = 32; // SHA-256 output length
    private const int Iterations = 100_000;

    public static string Hash(string plainTextKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainTextKey);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(plainTextKey),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return string.Concat(
            HashVersionPrefix, Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "$", Convert.ToBase64String(salt),
            "$", Convert.ToBase64String(hash));
    }

    /// <summary>
    /// Verifies a plain-text key against a stored hash in constant time for the matching input;
    /// the hash-format comparison itself is structural, not secret.
    /// </summary>
    public static bool Verify(string plainTextKey, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(plainTextKey) || string.IsNullOrWhiteSpace(storedHash))
            return false;

        return storedHash.StartsWith(HashVersionPrefix, StringComparison.Ordinal)
            ? VerifyPbkdf2(plainTextKey, storedHash)
            : VerifyLegacySha256(plainTextKey, storedHash);
    }

    /// <summary>True when the stored hash uses the current versioned format.</summary>
    public static bool IsVersionedHash(string storedHash)
        => storedHash.StartsWith(HashVersionPrefix, StringComparison.Ordinal);

    private static bool VerifyPbkdf2(string plainTextKey, string storedHash)
    {
        var parts = storedHash.Split('$');
        if (parts.Length != 5 || !int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var iterations))
            return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(plainTextKey),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool VerifyLegacySha256(string plainTextKey, string storedHash)
    {
        var computed = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(plainTextKey)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(storedHash),
            Encoding.UTF8.GetBytes(computed));
    }
}