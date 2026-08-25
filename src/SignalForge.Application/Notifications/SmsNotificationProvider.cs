using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SignalForge.Application.Notifications;

/// <summary>
/// Simulated SMS provider. Produces a provider-assigned delivery id and a structured log line as a
/// stand-in for a real SMS gateway behind the same contract.
/// </summary>
public class SmsNotificationProvider : INotificationProvider
{
    private readonly ILogger<SmsNotificationProvider> _logger;

    public SmsNotificationProvider(ILogger<SmsNotificationProvider> logger)
    {
        _logger = logger;
    }

    public string ProviderType => "sms";

    public Task<NotificationDeliveryResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var deliveryId = $"sms-{Guid.NewGuid():N}";

        _logger.LogInformation(
            "SMS notification delivered to {Recipient} with deliveryId {deliveryId} | Body: {Body}",
            message.Recipient, deliveryId, message.Body);

        return Task.FromResult(new NotificationDeliveryResult(
            deliveryId,
            ProviderType,
            NotificationDeliveryStatus.Accepted,
            DateTime.UtcNow));
    }
}