using System.Threading;
using System.Threading.Tasks;
using SignalForge.Application.Broker;

namespace SignalForge.Worker.Services
{
    /// <summary>
    /// Sends a claimed outbox message through the active <see cref="IMessageBroker"/> transport.
    /// </summary>
    public interface IOutboxMessageSender
    {
        Task SendAsync(string type, string payload, CancellationToken cancellationToken = default);
    }
}