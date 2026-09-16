using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Application.Services;

/// <summary>
/// A real, executable retryable operation. Operations are resolved by <see cref="OperationType"/>
/// and dispatched by <see cref="RetryableOperationStepProcessor"/>; concrete implementations live
/// next to whatever they call (a database, an HTTP client, or a pure in-process routine) and must
/// be registered in the <see cref="IRetryableOperationRegistry"/>. A throw is treated as an
/// operation failure and schedules a retry; there is no random success simulation.
/// </summary>
public interface IRetryableOperation
{
    string OperationType { get; }

    Task<RetryableOperationResult> ExecuteAsync(
        string parametersJson,
        CancellationToken cancellationToken = default);
}