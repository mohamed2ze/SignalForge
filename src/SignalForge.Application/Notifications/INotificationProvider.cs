using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Application.Notifications;

/// <summary>
/// Deliver a notification message. Implementations are transport-specific (email, SMS, webhook)
/// and are resolved by provider type through the <see cref="INotificationProviderRegistry"/>.
/// </summary>
public interface INotificationProvider
{
    string ProviderType { get; }

    Task<NotificationDeliveryResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}