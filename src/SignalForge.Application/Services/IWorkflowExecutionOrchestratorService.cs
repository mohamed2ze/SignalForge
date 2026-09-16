using System.Threading;
using System.Threading.Tasks;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services
{
    public interface IWorkflowExecutionOrchestratorService
    {
        Task<WorkflowExecution> StartWorkflowExecutionAsync(
            Guid workflowId,
            Guid workflowVersionId,
            Guid eventId,
            Guid tenantId,
            CancellationToken cancellationToken = default);

        Task<bool> AdvanceWorkflowExecutionAsync(Guid executionId, CancellationToken cancellationToken = default);

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