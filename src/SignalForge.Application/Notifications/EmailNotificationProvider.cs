using System;
using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Application.Notifications;

/// <summary>
/// Real email provider: delegates to an <see cref="IOutboundEmailTransport"/> (SMTP in production)
/// and returns the transport-assigned delivery id. Logs the recipient and delivery id only —
/// subjects and bodies are never written to logs.
/// </summary>
public class EmailNotificationProvider : INotificationProvider
{
    private readonly IOutboundEmailTransport _transport;

    public EmailNotificationProvider(IOutboundEmailTransport transport)
    {
        _transport = transport;
    }

    public string ProviderType => "email";

    public async Task<NotificationDeliveryResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var deliveryId = await _transport.SendAsync(
            message.Recipient, message.Subject, message.Body, cancellationToken);

        return new NotificationDeliveryResult(
            deliveryId,
            ProviderType,
            NotificationDeliveryStatus.Accepted,
            DateTime.UtcNow);
    }
}