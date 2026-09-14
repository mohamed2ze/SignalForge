using System.Net;

namespace SignalForge.Application.Security;

/// <summary>
/// Delegating handler placed in the outbound webhook HttpClient pipeline. Re-validates every
/// request's URL (absolute https URI, no localhost, no literal IP in a blocked range) and resolves
/// the host at request time to reject private, loopback, link-local and cloud-metadata addresses.
/// Redirect-following is disabled at the handler level, so 3xx responses are never followed to a
/// different target.
/// </summary>
public sealed class OutboundHttpRequestGuardHandler : DelegatingHandler
{
    private readonly OutboundWebhookOptions _options;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _hostResolver;

    public OutboundHttpRequestGuardHandler(
        OutboundWebhookOptions options,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostResolver = null)
    {
        _options = options;
        _hostResolver = hostResolver ?? ((host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.RequestUri, nameof(request.RequestUri));

        var uri = OutboundUrlValidator.Validate(request.RequestUri.OriginalString, _options);
        await OutboundUrlValidator.EnsureSafeHostAsync(uri, _hostResolver, cancellationToken).ConfigureAwait(false);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}