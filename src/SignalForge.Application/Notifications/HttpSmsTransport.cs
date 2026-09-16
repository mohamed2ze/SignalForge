using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalForge.Application.Security;

namespace SignalForge.Application.Notifications;

/// <summary>
/// Real HTTP SMS transport. POSTs an RFC-compatible JSON body to the configured gateway with the
/// API key in a configurable header (default <c>X-Api-Key</c>). The endpoint URL passes through
/// the shared outbound SSRF guard, response bodies are size-capped, and the transport fails closed
/// when no gateway URL is configured. The gateway-assigned id is read from the <c>sid</c> field,
/// falling back to a provider-generated id.
/// </summary>
public class HttpSmsTransport : IOutboundSmsTransport
{
    private readonly HttpClient _httpClient;
    private readonly SmsNotificationOptions _options;
    private readonly OutboundWebhookOptions _outboundOptions;
    private readonly ILogger<HttpSmsTransport> _logger;

    public HttpSmsTransport(
        HttpClient httpClient,
        IOptions<SmsNotificationOptions> options,
        IOptions<OutboundWebhookOptions> outboundOptions,
        ILogger<HttpSmsTransport> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _outboundOptions = outboundOptions.Value;
        _logger = logger;
    }

    public async Task<string> SendAsync(
        string to,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiBaseUrl))
        {
            throw new NotificationDeliveryException(
                "SMS delivery is not configured (Notifications:Sms:ApiBaseUrl is empty).");
        }

        Uri endpoint;
        try
        {
            endpoint = OutboundUrlValidator.Validate(_options.ApiBaseUrl, _outboundOptions);
        }
        catch (InvalidOperationException ex)
        {
            throw new NotificationDeliveryException(ex.Message);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrEmpty(_options.ApiKey))
            request.Headers.TryAddWithoutValidation(_options.ApiKeyHeader, _options.ApiKey);

        request.Content = JsonContent.Create(
            new { to, message = body, from = _options.FromPhone });

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
            {
                var errorBody = await OutboundResponseBody.ReadCappedAsync(
                    response.Content, _outboundOptions.MaxResponseBytes, cancellationToken);
                throw new NotificationDeliveryException(
                    $"SMS gateway rejected the message with status {(int)response.StatusCode}: {errorBody}");
            }

            var deliveryId = await ReadGatewayIdAsync(
                response, _outboundOptions.MaxResponseBytes, cancellationToken);
            if (string.IsNullOrWhiteSpace(deliveryId))
                deliveryId = $"sms-{Guid.NewGuid():N}";

            _logger.LogInformation(
                "SMS notification delivered to {Recipient} with deliveryId {deliveryId} (HTTP {StatusCode})",
                to, deliveryId, (int)response.StatusCode);

            return deliveryId;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NotificationDeliveryException("SMS gateway request timed out.");
        }
        catch (InvalidOperationException ex)
        {
            throw new NotificationDeliveryException(ex.Message);
        }
        catch (NotificationDeliveryException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new NotificationDeliveryException($"SMS gateway transport error: {ex.Message}", ex);
        }
    }

    private static async Task<string?> ReadGatewayIdAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = await OutboundResponseBody.ReadCappedAsync(
                response.Content, maxBytes, cancellationToken);
            var node = JsonNode.Parse(text);
            return node?["sid"]?.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }
}