using System.Threading;
using System.Threading.Tasks;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services
{
    /// <summary>
    /// Service for publishing messages to the outbox for reliable delivery.
    /// </summary>
    public interface IOutboxPublisher
    {
        /// <summary>
        /// Publishes a message to the outbox within the current transaction.
        /// </summary>
        /// <param name="tenantId">The tenant the message belongs to</param>
        /// <param name="type">The message type (used for routing or handling)</param>
        /// <param name="payload">The message payload (typically JSON)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the asynchronous operation</returns>
        Task PublishAsync(Guid tenantId, string type, string payload, CancellationToken cancellationToken = default);
    }
}