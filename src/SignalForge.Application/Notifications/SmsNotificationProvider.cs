using System;
using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Application.Notifications;

/// <summary>
/// Real SMS provider: delegates to an <see cref="IOutboundSmsTransport"/> (HTTP gateway in
/// production) and returns the gateway-assigned delivery id. Logs the recipient and delivery id
/// only — message bodies are never written to logs.
/// </summary>
public class SmsNotificationProvider : INotificationProvider
{
    private readonly IOutboundSmsTransport _transport;

    public SmsNotificationProvider(IOutboundSmsTransport transport)
    {
        _transport = transport;
    }

    public string ProviderType => "sms";

    public async Task<NotificationDeliveryResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var deliveryId = await _transport.SendAsync(
            message.Recipient, message.Body, cancellationToken);

        return new NotificationDeliveryResult(
            deliveryId,
            ProviderType,
            NotificationDeliveryStatus.Accepted,
            DateTime.UtcNow);
    }
}