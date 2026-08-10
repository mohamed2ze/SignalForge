using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Worker.Services
{
    /// <summary>
    /// Interface for sending outbox messages to their destination.
    /// In a real implementation, this would send to a message broker like RabbitMQ, Kafka, etc.
    /// For this implementation, we'll simulate sending by logging.
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