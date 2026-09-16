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
        /// Stages a message to the outbox within the current unit of work. The message is NOT
        /// persisted by this call: the caller commits it with its own <c>SaveChangesAsync</c>, so
        /// an outbox write can be committed atomically with the business change that produced it
        /// (e.g. an emission step's success transition). Callers that have no surrounding unit of
        /// work — such as the tests and standalone publish paths — must call
        /// <c>SaveChangesAsync</c> themselves for the message to become durable.
        /// </summary>
        /// <param name="tenantId">The tenant the message belongs to</param>
        /// <param name="type">The message type (used for routing or handling)</param>
        /// <param name="payload">The message payload (typically JSON)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the asynchronous operation</returns>
        Task PublishAsync(Guid tenantId, string type, string payload, CancellationToken cancellationToken = default);
    }
}