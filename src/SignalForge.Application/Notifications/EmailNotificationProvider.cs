using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SignalForge.Application.Notifications;

/// <summary>
/// Simulated email provider. Does not talk to an SMTP server: it produces a provider-assigned
/// delivery id and a structured log line, standing in for a real email transport behind the same
/// contract. Swap the registration for a real adapter later without touching step configurations.
/// </summary>
public class EmailNotificationProvider : INotificationProvider
{
    private readonly ILogger<EmailNotificationProvider> _logger;

    public EmailNotificationProvider(ILogger<EmailNotificationProvider> logger)
    {
        _logger = logger;
    }

    public string ProviderType => "email";

    public Task<NotificationDeliveryResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var deliveryId = $"email-{Guid.NewGuid():N}";

        _logger.LogInformation(
            "Email notification delivered to {Recipient} with deliveryId {deliveryId} | Subject: {Subject}",
            message.Recipient, deliveryId, message.Subject ?? "(no subject)");

        return Task.FromResult(new NotificationDeliveryResult(
            deliveryId,
            ProviderType,
            NotificationDeliveryStatus.Accepted,
            DateTime.UtcNow));
    }
}