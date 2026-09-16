using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Worker.Services
{
    public interface IOutboxProcessor
    {
        Task<TimeSpan> ProcessBatchAsync(CancellationToken cancellationToken = default);
    }
}