using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Application.Notifications;

/// <summary>
/// A real outbound SMS transport. Implementations POST to an actual SMS gateway (see
/// <see cref="HttpSmsTransport"/>); the notification provider never logs message content.
/// </summary>
public interface IOutboundSmsTransport
{
    /// <summary>
    /// Sends <paramref name="body"/> to <paramref name="to"/> and returns a gateway-assigned
    /// delivery id. Throws <see cref="NotificationDeliveryException"/> on any failure.
    /// </summary>
    Task<string> SendAsync(string to, string body, CancellationToken cancellationToken = default);
}