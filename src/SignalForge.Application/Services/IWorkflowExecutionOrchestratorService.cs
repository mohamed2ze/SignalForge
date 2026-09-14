using System.Threading;
using System.Threading.Tasks;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services
{
    /// <summary>
    /// Service for orchestrating workflow executions: starting, advancing steps, handling retries.
    /// </summary>
    public interface IWorkflowExecutionOrchestratorService
    {
        /// <summary>
        /// Starts a new workflow execution.
        /// </summary>
        /// <param name="workflowId">The workflow ID</param>
        /// <param name="workflowVersionId">The workflow version ID to execute</param>
        /// <param name="eventId">The event ID that triggered this execution</param>
        /// <param name="tenantId">The tenant ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The created workflow execution</returns>
        Task<WorkflowExecution> StartWorkflowExecutionAsync(
            Guid workflowId,
            Guid workflowVersionId,
            Guid eventId,
            Guid tenantId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Advances the workflow execution to the next step.
        /// </summary>
        /// <param name="executionId">The workflow execution ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if advancement was successful, false if execution is already completed or not found</returns>
        Task<bool> AdvanceWorkflowExecutionAsync(Guid executionId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a workflow execution by ID for a specific tenant.
        /// </summary>
        /// <param name="executionId">The workflow execution ID</param>
        /// <param name="tenantId">The tenant ID</param>
        /// <returns>The workflow execution if found and belongs to the tenant, otherwise null</returns>
        Task<WorkflowExecution?> GetWorkflowExecutionByIdAsync(Guid executionId, Guid tenantId);

        /// <summary>
        /// Schedules an immediate retry of a failed step execution within the tenant's execution,
        /// applying the same exponential-backoff policy the worker uses so the retry is picked up on
        /// the next advancement cycle. The step must exist, belong to the tenant, and be eligible
        /// (failed and not exhausted) for a retry to be scheduled.
        /// </summary>
        /// <param name="executionId">The workflow execution ID</param>
        /// <param name="stepExecutionId">The step execution ID to retry</param>
        /// <param name="tenantId">The tenant ID</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The scheduling outcome, with the updated step execution when scheduled</returns>
        Task<ScheduleStepRetryResult> ScheduleStepRetryAsync(
            Guid executionId,
            Guid stepExecutionId,
            Guid tenantId,
            CancellationToken cancellationToken = default);
    }
}