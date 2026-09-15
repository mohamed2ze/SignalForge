using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SignalForge.Application.Data;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services
{
    /// <summary>
    /// Implementation of workflow execution orchestrator service. Owns the execution lifecycle
    /// (start, read) and manual retry eligibility; step advancement is delegated to
    /// <see cref="IWorkflowExecutionAdvancer"/> and the shared retry policy to
    /// <see cref="IStepExecutionRetryPolicy"/>.
    /// </summary>
    public class WorkflowExecutionOrchestratorService : IWorkflowExecutionOrchestratorService
    {
        private readonly ISignalForgeDbContext _dbContext;
        private readonly IWorkflowExecutionAdvancer _advancer;
        private readonly IStepExecutionRetryPolicy _retryPolicy;
        private readonly ILogger<WorkflowExecutionOrchestratorService> _logger;

        public WorkflowExecutionOrchestratorService(
            ISignalForgeDbContext dbContext,
            IWorkflowExecutionAdvancer advancer,
            IStepExecutionRetryPolicy retryPolicy,
            ILogger<WorkflowExecutionOrchestratorService> logger)
        {
            _dbContext = dbContext;
            _advancer = advancer;
            _retryPolicy = retryPolicy;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<WorkflowExecution> StartWorkflowExecutionAsync(
            Guid workflowId,
            Guid workflowVersionId,
            Guid eventId,
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            // Validate that the workflow exists and belongs to the tenant
            var workflow = await _dbContext.Workflows
                .FirstOrDefaultAsync(w => w.Id == workflowId && w.TenantId == tenantId && w.DeletedAt == null, cancellationToken);

            if (workflow == null)
            {
                throw new InvalidOperationException($"Workflow {workflowId} not found for tenant {tenantId}");
            }

            // Validate that the workflow version exists and belongs to the workflow
            var workflowVersion = await _dbContext.WorkflowVersions
                .FirstOrDefaultAsync(v => v.Id == workflowVersionId && v.WorkflowId == workflowId, cancellationToken);

            if (workflowVersion == null)
            {
                throw new InvalidOperationException($"Workflow version {workflowVersionId} not found for workflow {workflowId}");
            }

            // Domain invariant: only published versions are executable
            if (!workflowVersion.IsPublished)
            {
                throw new InvalidOperationException($"Workflow version {workflowVersionId} is not published");
            }

            // Validate that the event exists and belongs to the tenant
            var @event = await _dbContext.Events
                .FirstOrDefaultAsync(e => e.Id == eventId && e.TenantId == tenantId, cancellationToken);

            if (@event == null)
            {
                throw new InvalidOperationException($"Event {eventId} not found for tenant {tenantId}");
            }

            // Create the workflow execution
            var execution = WorkflowExecution.Create(workflowId, workflowVersionId, eventId, tenantId);
            execution.Start();

            _dbContext.WorkflowExecutions.Add(execution);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return execution;
        }

        /// <inheritdoc />
        public Task<bool> AdvanceWorkflowExecutionAsync(Guid executionId, CancellationToken cancellationToken = default)
            => _advancer.AdvanceWorkflowExecutionAsync(executionId, cancellationToken);

        /// <inheritdoc />
        public async Task<ScheduleStepRetryResult> ScheduleStepRetryAsync(
            Guid executionId,
            Guid stepExecutionId,
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            var execution = await _dbContext.WorkflowExecutions
                .Include(e => e.StepExecutions)
                .FirstOrDefaultAsync(e => e.Id == executionId && e.TenantId == tenantId, cancellationToken);

            if (execution is null)
                return new ScheduleStepRetryResult(StepRetryStatus.NotFound, null);

            var stepExecution = execution.StepExecutions.FirstOrDefault(se => se.Id == stepExecutionId);
            if (stepExecution is null)
                return new ScheduleStepRetryResult(StepRetryStatus.NotFound, null);

            // In-flight, or a scheduled retry whose window isn't due yet, is owned by the worker —
            // double-booking would race the advancement cycle.
            if (stepExecution.Status == WorkflowStepExecutionStatus.Pending ||
                stepExecution.Status == WorkflowStepExecutionStatus.Running ||
                (stepExecution.Status == WorkflowStepExecutionStatus.Retrying &&
                 !stepExecution.IsTimeToRetry()))
                return new ScheduleStepRetryResult(StepRetryStatus.NotEligible, stepExecution);

            // Succeeded/cancelled steps have nothing to retry; exhausted attempts cannot re-arm.
            if (stepExecution.Status == WorkflowStepExecutionStatus.Succeeded ||
                stepExecution.Status == WorkflowStepExecutionStatus.Cancelled ||
                !stepExecution.CanRetry())
                return new ScheduleStepRetryResult(StepRetryStatus.NotEligible, stepExecution);

            await _retryPolicy.ScheduleRetryAsync(stepExecution, cancellationToken);
            return new ScheduleStepRetryResult(StepRetryStatus.Scheduled, stepExecution);
        }

        /// <inheritdoc />
        public async Task<WorkflowExecution?> GetWorkflowExecutionByIdAsync(Guid executionId, Guid tenantId)
        {
            // Include step executions so callers (e.g. the API step list endpoint) observe the
            // progress made by the worker after advance. Without this the navigation stays empty.
            return await _dbContext.WorkflowExecutions
                .Include(e => e.StepExecutions)
                .FirstOrDefaultAsync(e => e.Id == executionId && e.TenantId == tenantId);
        }
    }
}