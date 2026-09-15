using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Application.Services;

/// <summary>
/// Advances a workflow execution one step, dispatching each step to its processor through the
/// registry. This is the forward-progress half of execution orchestration (the "what runs next"
/// decision); lifecycle start/read/retry-eligibility stay on
/// <see cref="IWorkflowExecutionOrchestratorService"/>.
/// </summary>
public interface IWorkflowExecutionAdvancer
{
    /// <summary>
    /// Advances the workflow execution to its next step.
    /// </summary>
    /// <param name="executionId">The workflow execution ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the execution made progress, false if it is not found, already completed,
    /// or waiting on an in-flight/retry-window step.</returns>
    Task<bool> AdvanceWorkflowExecutionAsync(Guid executionId, CancellationToken cancellationToken = default);
}