using System.Threading;
using System.Threading.Tasks;
using SignalForge.Application.Broker;

namespace SignalForge.Worker.Services
{
    /// <summary>
    /// Interface for sending outbox messages to their destination.
    /// Implementations publish through an <see cref="IMessageBroker"/> so the worker can hand
    /// claimed messages to the broker transport (in-memory for local dev; durable transports
    /// are a swap-in behind the same broker abstraction).
    /// </summary>
    public interface IOutboxMessageSender
    {
        /// <summary>
        /// Sends an outbox message to its destination.
        /// </summary>
        /// <param name="type">The message type</param>
        /// <param name="payload">The message payload</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the asynchronous operation</returns>
        Task SendAsync(string type, string payload, CancellationToken cancellationToken = default);
    }
}