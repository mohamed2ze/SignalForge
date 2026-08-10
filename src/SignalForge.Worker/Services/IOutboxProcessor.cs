using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Worker.Services
{
    /// <summary>
    /// Interface for processing outbox messages for reliable delivery.
    /// </summary>
    public interface IOutboxProcessor
    {
        /// <summary>
        /// Processes a single batch of unprocessed outbox messages.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The delay to wait before the next poll cycle, computed from
        /// success/failure backoff and circuit-breaker state.</returns>
        Task<TimeSpan> ProcessBatchAsync(CancellationToken cancellationToken = default);
    }
}