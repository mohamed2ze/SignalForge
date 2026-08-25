using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Application.Notifications;

/// <summary>
/// Deliver a notification message. Implementations are transport-specific (email, SMS, webhook)
/// and are resolved by provider type through the <see cref="INotificationProviderRegistry"/>.
/// </summary>
public interface INotificationProvider
{
    /// <summary>
    /// Stable provider type key used in step configuration and registry lookups
    /// (e.g. "email", "sms", "webhook"). Case-insensitive.
    /// </summary>
    string ProviderType { get; }

    /// <summary>
    /// Sends the message. Throws <see cref="NotificationDeliveryException"/> on delivery failure.
    /// </summary>
    Task<NotificationDeliveryResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}