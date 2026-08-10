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
        /// Handles a failed step execution by retrying or marking as failed.
        /// </summary>
        /// <param name="executionId">The workflow execution ID</param>
        /// <param name="stepExecutionId">The step execution ID that failed</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if the execution should be retried, false if it should be marked as failed</returns>
        Task<bool> HandleStepExecutionFailureAsync(
            Guid executionId,
            Guid stepExecutionId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a workflow execution by ID for a specific tenant.
        /// </summary>
        /// <param name="executionId">The workflow execution ID</param>
        /// <param name="tenantId">The tenant ID</param>
        /// <returns>The workflow execution if found and belongs to the tenant, otherwise null</returns>
        Task<WorkflowExecution?> GetWorkflowExecutionByIdAsync(Guid executionId, Guid tenantId);
    }
}