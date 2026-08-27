using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SignalForge.Application.Security;

namespace SignalForge.IntegrationTests;

/// <summary>
/// Client helpers for webhook event ingestion: every POST /api/events request must carry the
/// X-SignalForge-Timestamp and X-SignalForge-Signature headers (Decision #23). The signature is
/// computed over the EXACT raw body bytes that are sent, so tests serialize once and reuse that
/// string for both the request and the HMAC.
/// </summary>
public static class EventSigner
{
    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Serializes an object to the camelCase JSON wire format used by the API.
    /// </summary>
    public static string CamelJson<T>(T value) => JsonSerializer.Serialize(value, CamelCase);

    /// <summary>
    /// Posts raw JSON to /api/events with a valid signature computed from the same raw bytes.
    /// Returns the unadorned request so callers can assert on the response.
    /// </summary>
    public static async Task<HttpResponseMessage> PostSignedEventAsync(
        HttpClient client,
        string secret,
        string rawJson)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = EventSignatureVerifier.ComputeSignature(secret, timestamp, rawJson);
        using var content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        content.Headers.Add(EventSignatureVerifier.TimestampHeader, timestamp.ToString());
        content.Headers.Add(EventSignatureVerifier.SignatureHeader, signature);
        return await client.PostAsync("/api/events", content);
    }

    /// <summary>
    /// Builds the two signature headers without posting, for tampering/replay negative tests where
    /// the header values are deliberately wrong.
    /// </summary>
    public static StringContent SignedContent(string secret, string rawJson, long? timestampUnixSeconds = null)
    {
        var timestamp = timestampUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = EventSignatureVerifier.ComputeSignature(secret, timestamp, rawJson);
        var content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        content.Headers.Add(EventSignatureVerifier.TimestampHeader, timestamp.ToString());
        content.Headers.Add(EventSignatureVerifier.SignatureHeader, signature);
        return content;
    }
}