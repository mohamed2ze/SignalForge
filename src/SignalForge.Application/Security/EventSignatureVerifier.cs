using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SignalForge.Application.Security;

/// <summary>
/// HMAC-SHA256 signature scheme for inbound webhook events (Decision #23).
///
/// Clients sign the raw request body exactly as transmitted:
///   X-SignalForge-Timestamp: <unix seconds>
///   X-SignalForge-Signature: sha256=<lowercase hex HMAC-SHA256(secret, "{timestamp}:{rawBody}")>
///
/// The timestamp guards against replay (a freshness window is enforced) and its inclusion in the
/// MAC ties the signature to a moment in time. Verification recomputes the MAC over the same raw
/// bytes and compares in constant time (<see cref="CryptographicOperations.FixedTimeEquals"/>), so
/// a length/timing side channel cannot leak information about the expected signature.
/// </summary>
public static class EventSignatureVerifier
{
    public const string TimestampHeader = "X-SignalForge-Timestamp";
    public const string SignatureHeader = "X-SignalForge-Signature";
    public const string SchemeId = "sha256";

    /// <summary>Maximum accepted drift between the signed timestamp and the server clock.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Computes the signature a client must send for a given secret, timestamp and raw body.
    /// </summary>
    public static string ComputeSignature(string secret, long timestampUnixSeconds, string rawBody)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var payload = Encoding.UTF8.GetBytes($"{timestampUnixSeconds}:{rawBody}");
        var hash = hmac.ComputeHash(payload);
        return $"{SchemeId}=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Parses a Unix-seconds timestamp header value.
    /// </summary>
    public static bool TryParseUnixSeconds(string? value, out long timestampUnixSeconds)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out timestampUnixSeconds);

    /// <summary>
    /// Verifies a signature header against the raw body and secret, enforcing the freshness window.
    /// The whole comparison is constant-time on the MAC; the timestamp check is a simple time
    /// comparison (not secret material).
    /// </summary>
    public static bool Verify(
        string secret,
        long timestampUnixSeconds,
        string rawBody,
        string? signatureHeaderValue,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(signatureHeaderValue))
            return false;

        // Freshness: reject stale or future-dated signatures (replay protection).
        var current = now ?? DateTimeOffset.UtcNow;
        if (Math.Abs((current - DateTimeOffset.FromUnixTimeSeconds(timestampUnixSeconds)).TotalSeconds) >
            MaxClockSkew.TotalSeconds)
        {
            return false;
        }

        if (!signatureHeaderValue.StartsWith(SchemeId + "=", StringComparison.OrdinalIgnoreCase))
            return false;

        var expected = ComputeSignature(secret, timestampUnixSeconds, rawBody);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signatureHeaderValue));
    }
}