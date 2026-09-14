using System.Net;
using System.Net.Sockets;

namespace SignalForge.Application.Security;

/// <summary>
/// Validates outbound webhook URLs against SSRF targets before a request is issued.
/// <para>
/// The checks surfaced directly on the processors are synchronous and DNS-free: absolute-URI and
/// scheme enforcement, IP-literal handling and the bare "localhost" hostname. Hostnames that must
/// be resolved to IPs are re-checked at request time by
/// <see cref="OutboundHttpRequestGuardHandler"/>, which blocks private, loopback, link-local and
/// cloud-metadata addresses even when a DNS name resolves to one.
/// </para>
/// </summary>
public static class OutboundUrlValidator
{
    /// <summary>
    /// Validates a webhook URL synchronously. Throws <see cref="InvalidOperationException"/> when
    /// the URL is not an absolute https URL (http is only allowed for hosts explicitly listed in
    /// <see cref="OutboundWebhookOptions.AllowedHttpHosts"/>), targets localhost, or is an IP
    /// literal in a blocked range.
    /// </summary>
    public static Uri Validate(string url, OutboundWebhookOptions options)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(options);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Outbound webhook URL must be an absolute URI, got '{url}'.");

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            var allowed = options.AllowedHttpHosts.Contains(uri.DnsSafeHost, StringComparer.OrdinalIgnoreCase);
            if (!allowed)
                throw new InvalidOperationException(
                    $"Outbound webhook URL must use https; http is only permitted for allowlisted hosts ('{uri.DnsSafeHost}').");
        }

        if (string.Equals(uri.DnsSafeHost, "localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Outbound webhook URL must not target localhost.");

        if (IsLiteralIp(uri.DnsSafeHost, out var literalIp))
            EnsurePublicAddress(literalIp);

        return uri;
    }

    /// <summary>
    /// Resolves the request host and ensures none of the returned addresses are SSRF targets.
    /// <paramref name="resolver"/> defaults to the system DNS and is injectable for tests.
    /// </summary>
    public static async Task EnsureSafeHostAsync(
        Uri requestUri,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestUri);

        var host = requestUri.DnsSafeHost;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Outbound webhook URL must not target localhost.");

        if (IsLiteralIp(host, out var literalIp))
        {
            EnsurePublicAddress(literalIp);
            return;
        }

        var addresses = resolver is null
            ? await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false)
            : await resolver(host, cancellationToken).ConfigureAwait(false);

        foreach (var address in addresses)
            EnsurePublicAddress(address);
    }

    /// <summary>Returns true when <paramref name="address"/> is loopback, private, link-local,
    /// CGNAT, multicast, or otherwise non-routable from the public internet.</summary>
    public static bool IsPrivateOrReserved(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (IPAddress.IsLoopback(ip))
            return true;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 10/8, 172.16/12, 192.168/16 private ranges.
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            // 169.254/16 link-local (covers the 169.254.169.254 cloud-metadata addresses).
            if (b[0] == 169 && b[1] == 254) return true;
            // 100.64/10 carrier-grade NAT.
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            // 0/8 current-network and 224/4 multicast.
            return b[0] == 0 || b[0] >= 224;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            // fc00::/7 unique-local, fe80::/10 link-local, ff00::/8 multicast.
            if ((b[0] & 0xFE) == 0xFC) return true;
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;
            if (b[0] == 0xFF) return true;
            return false;
        }

        return true;
    }

    private static bool IsLiteralIp(string host, out IPAddress address)
        => IPAddress.TryParse(host, out address!);

    private static void EnsurePublicAddress(IPAddress address)
    {
        if (IsPrivateOrReserved(address))
            throw new InvalidOperationException(
                $"Outbound webhook URL must not target a non-public address (blocked '{address}').");
    }

    /// <summary>
    /// Redacts an outbound URL for logging: userinfo, query and fragment are stripped, leaving a
    /// scheme://host[/path] form so credentials never reach structured logs.
    /// </summary>
    public static string ScrubForLog(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "<invalid-url>";

        return ScrubForLog(uri);
    }

    /// <inheritdoc cref="ScrubForLog(string)"/>
    public static string ScrubForLog(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.AbsoluteUri;
    }
}