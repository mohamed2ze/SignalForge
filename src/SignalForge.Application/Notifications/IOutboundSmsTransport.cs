using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Application.Notifications;

/// <summary>
/// A real outbound SMS transport. Implementations POST to an actual SMS gateway (see
/// <see cref="HttpSmsTransport"/>); the notification provider never logs message content.
/// </summary>
public interface IOutboundSmsTransport
{
    Task<string> SendAsync(string to, string body, CancellationToken cancellationToken = default);
}