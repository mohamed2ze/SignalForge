using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Application.Services;

/// <summary>
/// A real retryable operation that echoes its parameters back as the operation output. Used by
/// workflow compositions that need a deterministic, verified side-effect-free operation; it is a
/// concrete registered operation, not a simulation.
/// </summary>
public class EchoRetryableOperation : IRetryableOperation
{
    /// <inheritdoc />
    public string OperationType => "echo";

    /// <inheritdoc />
    public Task<RetryableOperationResult> ExecuteAsync(
        string parametersJson,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RetryableOperationResult(
            Succeeded: true,
            Output: parametersJson));
    }
}