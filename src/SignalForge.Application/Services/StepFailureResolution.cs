using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

/// <summary>
/// Outcome of <see cref="IStepExecutionRetryPolicy.ResolveFailureAsync"/>.
/// </summary>
public enum StepFailureResolution
{
    /// <summary>The step has attempts remaining and was rescheduled with exponential backoff.</summary>
    Retrying,

    /// <summary>The step exhausted its attempts: a dead-letter message was created and the
    /// parent execution failed.</summary>
    DeadLettered
}