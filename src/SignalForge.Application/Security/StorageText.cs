using System.Text.RegularExpressions;

namespace SignalForge.Application.Security;

/// <summary>
/// Bounds text before it is persisted to storage columns. Columns are nvarchar(max) so nothing
/// truncates at the DB, but unbounded webhook responses, exception dumps or event payloads should
/// still be capped at the write boundary to keep the stored rows (and later reads) reasonable.
/// </summary>
public static class StorageText
{
    public const int DefaultMaxLength = 16 * 1024;

    public static string? TruncateForStorage(string? value, int maxLength = DefaultMaxLength)
        => value is null ? null : value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// Bounds <paramref name="value"/> to <paramref name="maxLength"/> characters, redacting
    /// secrets first so nothing sensitive reaches error/response columns: absolute URLs lose
    /// their userinfo/query/fragment, password-style parameter values and Authorization header
    /// values are masked, and JWT-shaped base64 blobs are replaced with a placeholder.
    /// </summary>
    public static string? ScrubForStorage(string? value, int maxLength = DefaultMaxLength)
    {
        if (value is null)
            return null;

        var redacted = Redact(value);
        return redacted.Length <= maxLength ? redacted : redacted[..maxLength];
    }

    private static string Redact(string value)
    {
        value = UrlRegex.Replace(value, m =>
        {
            var scrubbed = OutboundUrlValidator.ScrubForLog(m.Value);
            // ScrubForLog marks unparsable input; keep the original text in that case.
            return scrubbed == "<invalid-url>" ? m.Value : scrubbed;
        });
        value = ParameterRegex.Replace(value, "${1}***");
        value = AuthorizationRegex.Replace(value, "${1}***");
        value = JwtRegex.Replace(value, "<jwt-redacted>");
        return value;
    }

    private static readonly Regex UrlRegex = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ParameterRegex = new(
        @"(?i)([""']?[\w]*(?:password|passwd|pwd|token|secret|key)\b[""']?\s*[:=]\s*)(?:[""'][^""']*[""']|[^\s,;&'""<>{}]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AuthorizationRegex = new(
        @"(?i)([""']?authorization[""']?\s*:\s*)(?:[""'][^""']*[""']|Bearer\s+[^\s,;&'""<>{}]+|[^\s,;&'""<>{}]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JwtRegex = new(
        @"\beyJ[A-Za-z0-9_-]*(\.[A-Za-z0-9_-]+){2}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}