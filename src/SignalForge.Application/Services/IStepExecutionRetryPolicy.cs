using System.Threading;
using System.Threading.Tasks;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

/// <summary>
/// Owns the step-failure retry policy shared by every execution-advancement path: the worker's
/// pump (via the advancer) and the HTTP manual-retry endpoint. A failed step either schedules an
/// exponential-backoff retry (2^attempt seconds) in place, or — once attempts are exhausted —
/// creates exactly one dead-letter message and fails the parent execution.
/// </summary>
public interface IStepExecutionRetryPolicy
{
    /// <summary>
    /// Applies the retry/backoff/dead-letter decision to a step that just failed.
    /// </summary>
    /// <param name="execution">The parent execution, used for the dead-letter's context.</param>
    /// <param name="stepExecution">The failed step execution.</param>
    /// <param name="stepType">The step's type key, recorded on the dead-letter message.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Whether the step was rescheduled for retry or permanently dead-lettered.</returns>
    Task<StepFailureResolution> ResolveFailureAsync(
        WorkflowExecution execution,
        WorkflowStepExecution stepExecution,
        string stepType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules the exponential-backoff retry for a step: moves it to
    /// <see cref="WorkflowStepExecutionStatus.Retrying"/> with the new <c>NextRetryAt</c>.
    /// </summary>
    Task ScheduleRetryAsync(WorkflowStepExecution stepExecution, CancellationToken cancellationToken = default);
}