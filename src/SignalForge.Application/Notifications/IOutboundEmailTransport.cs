using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Application.Notifications;

/// <summary>
/// A real outbound email transport. Implementations talk to an actual mail server (see
/// <see cref="MailKitEmailTransport"/>); the notification provider never logs message content.
/// </summary>
public interface IOutboundEmailTransport
{
    /// <summary>
    /// Sends <paramref name="subject"/>/<paramref name="body"/> to <paramref name="to"/> and
    /// returns a transport/delivery id. Throws <see cref="NotificationDeliveryException"/> on any
    /// failure.
    /// </summary>
    Task<string> SendAsync(string to, string? subject, string body, CancellationToken cancellationToken = default);
}