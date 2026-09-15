using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Application.Services;

/// <summary>
/// The outcome of executing a retryable operation: whether it completed, an optional error
/// message for retry scheduling, and optional captured output recorded on the step execution.
/// </summary>
public sealed record RetryableOperationResult(bool Succeeded, string? ErrorMessage = null, string? Output = null);