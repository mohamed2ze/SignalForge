using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SignalForge.Application.Data;
using SignalForge.Application.Security;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;
using SignalForge.Domain.ValueObjects;

namespace SignalForge.Application.Services
{
    /// <summary>
    /// Implementation of workflow execution orchestrator service.
    /// </summary>
    public class WorkflowExecutionOrchestratorService : IWorkflowExecutionOrchestratorService
    {
        private readonly ISignalForgeDbContext _dbContext;
        private readonly IStepProcessorRegistry _stepProcessors;
        private readonly ILogger<WorkflowExecutionOrchestratorService> _logger;

        public WorkflowExecutionOrchestratorService(
            ISignalForgeDbContext dbContext,
            IStepProcessorRegistry stepProcessors,
            ILogger<WorkflowExecutionOrchestratorService> logger)
        {
            _dbContext = dbContext;
            _stepProcessors = stepProcessors;
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
        public async Task<bool> AdvanceWorkflowExecutionAsync(Guid executionId, CancellationToken cancellationToken = default)
        {
            // Get the workflow execution
            var execution = await _dbContext.WorkflowExecutions
                .Include(e => e.Event)
                .Include(e => e.StepExecutions)
                .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken);

            if (execution == null)
            {
                return false; // Execution not found
            }

            // Check if execution is already completed
            if (execution.IsCompleted())
            {
                return false; // Already completed
            }

            // Get the next step number to execute
            var nextStepNumber = execution.CurrentStepNumber + 1;

            // Get the workflow version to see how many steps it has
            var workflowVersion = await _dbContext.WorkflowVersions
                .Include(v => v.Steps)
                .FirstOrDefaultAsync(v => v.Id == execution.WorkflowVersionId, cancellationToken);

            if (workflowVersion == null)
            {
                return false; // Workflow version not found
            }

            // Check if there are more steps to execute
            var totalSteps = workflowVersion.Steps.Count;
            if (nextStepNumber > totalSteps)
            {
                // No more steps, mark execution as succeeded
                execution.Succeed();
                await _dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }

            // Get the next step to execute
            var nextStep = workflowVersion.Steps
                .FirstOrDefault(s => s.StepNumber == nextStepNumber && s.IsEnabled);

            if (nextStep == null)
            {
                // Step not found or disabled, skip to next step
                execution.AdvanceStep();
                await _dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }

            // Re-entry handling: a step execution may already exist for this position.
            // - In-flight (Pending/Running) or a retry that isn't due yet → busy, nothing to do.
            // - Failed with attempts remaining, or Retrying and due → retry in place (no duplicates).
            // - Failed with attempts exhausted → fail the execution.
            var existing = execution.StepExecutions
                .FirstOrDefault(se => se.StepNumber == nextStepNumber);

            WorkflowStepExecution stepExecution;
            if (existing != null)
            {
                if (existing.Status == WorkflowStepExecutionStatus.Pending ||
                    existing.Status == WorkflowStepExecutionStatus.Running ||
                    (existing.Status == WorkflowStepExecutionStatus.Retrying &&
                     !existing.IsTimeToRetry()))
                {
                    return false; // Busy or waiting for the retry window — skip this cycle
                }

                if (existing.Status == WorkflowStepExecutionStatus.Failed &&
                    !existing.CanRetry())
                {
                    // Exhausted all attempts for this step, fail the execution
                    execution.Fail(
                        $"Step {existing.StepNumber} failed after maximum retry attempts");
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    return true;
                }

                existing.PrepareForRetry();
                stepExecution = existing;
            }
            else
            {
                stepExecution = WorkflowStepExecution.Create(
                    execution.Id,
                    nextStep.Id,
                    nextStepNumber);

                execution.StepExecutions.Add(stepExecution);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            // Process the step using the appropriate step processor
            try
            {
                // Mark step as started
                stepExecution.Start();
                await _dbContext.SaveChangesAsync(cancellationToken);

                // Get the step processor for the step's type through the registry
                // (the same registry approach the notification providers use).
                var processor = _stepProcessors.Get(nextStep.StepType)
                    ?? throw new NotSupportedException($"Step type {nextStep.StepType} is not supported");

                // Build the runtime context (event payload + step outputs) used by conditional steps
                var context = new StepExecutionContext(BuildStepContext(execution));

                // Process the step
                bool shouldContinue = await processor.ProcessAsync(stepExecution, context, cancellationToken);

                if (shouldContinue)
                {
                    // If processing succeeded and we should continue to next step
                    // Check if the step was actually succeeded during processing
                    if (stepExecution.Status == WorkflowStepExecutionStatus.Succeeded)
                    {
                        if (stepExecution.RouteToStepNumber.HasValue)
                            execution.RouteToStep(stepExecution.RouteToStepNumber.Value);
                        else
                            execution.AdvanceStep();
                        await _dbContext.SaveChangesAsync(cancellationToken);
                    }
                    // If processing failed but should retry, the step execution will already be in retrying state
                    // If processing succeeded but we don't continue (unlikely), we still advance
                    else if (stepExecution.Status != WorkflowStepExecutionStatus.Succeeded &&
                             stepExecution.Status != WorkflowStepExecutionStatus.Retrying)
                    {
                        // If it's not succeeded or retrying, something went wrong - advance anyway to avoid infinite loop
                        execution.AdvanceStep();
                        await _dbContext.SaveChangesAsync(cancellationToken);
                    }
                }
                else
                {
                    // Processing indicates we should wait (e.g., for retry)
                    // The step execution status should already be set appropriately by the processor
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                // If processing throws an exception, mark the step as failed
                _logger.LogError(ex, "Error processing step {StepId} for execution {ExecutionId}",
                               stepExecution.Id, execution.Id);
                if (!stepExecution.IsCompleted())
                {
                    stepExecution.Fail(StorageText.ScrubForStorage(ex.ToString())!);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }

                // One shared retry/backoff/dead-letter path drives both this catch and the
                // HTTP retry endpoint, so a step exhausts attempts identically everywhere.
                return await ScheduleRetryAndMaybeDeadLetterAsync(
                    execution, stepExecution, nextStep.StepType, cancellationToken);
            }

            return true;
        }

        /// <summary>
        /// Shared retry/backoff/dead-letter decision for a failed step execution: when the step can
        /// still retry, schedules an exponential-backoff retry (2^attempt seconds) and marks the
        /// step <see cref="WorkflowStepExecutionStatus.Retrying"/>; otherwise fails the step, creates
        /// exactly one dead-letter message, and fails the parent execution.
        /// </summary>
        /// <returns>True when the step should be retried, false once it is permanently failed.</returns>
        private async Task<bool> ScheduleRetryAndMaybeDeadLetterAsync(
            WorkflowExecution execution,
            WorkflowStepExecution stepExecution,
            string stepType,
            CancellationToken cancellationToken)
        {
            if (stepExecution.CanRetry())
            {
                await ScheduleRetryAsync(stepExecution, cancellationToken);
                return true; // Should be retried
            }

            // Max attempts reached: create the dead letter and fail the execution permanently.
            _dbContext.DeadLetterMessages.Add(
                DeadLetterMessage.CreateFromFailedStep(stepExecution, execution, stepType));

            var allStepsCompleted = execution.StepExecutions.All(se => se.IsCompleted());
            execution.Fail(allStepsCompleted
                ? "One or more steps failed after maximum retry attempts"
                : $"Step {stepExecution.StepNumber} failed after maximum retry attempts");

            await _dbContext.SaveChangesAsync(cancellationToken);
            return false; // Should not be retried
        }

        /// <summary>
        /// Shared exponential-backoff retry used by both the advancement path and the HTTP retry
        /// endpoint (L6): backoff is 2^attempt seconds, and the step moves to
        /// <see cref="WorkflowStepExecutionStatus.Retrying"/> with the new <c>NextRetryAt</c>.
        /// </summary>
        private async Task ScheduleRetryAsync(
            WorkflowStepExecution stepExecution,
            CancellationToken cancellationToken)
        {
            var retryDelay = TimeSpan.FromSeconds(Math.Pow(2, stepExecution.AttemptNumber));
            stepExecution.Retry(DateTime.UtcNow.Add(retryDelay));
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

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

            await ScheduleRetryAsync(stepExecution, cancellationToken);
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

        private static JsonObject BuildStepContext(WorkflowExecution execution)
        {
            var @event = execution.Event;
            var eventNode = new JsonObject
            {
                ["type"] = JsonValue.Create(@event.EventType),
                ["externalEventId"] = JsonValue.Create(@event.ExternalEventId),
                ["occurredAt"] = JsonValue.Create(@event.OccurredAt),
                ["receivedAt"] = JsonValue.Create(@event.ReceivedAt),
                ["payload"] = TryParseAsJson(@event.Payload)
            };

            var outputsNode = new JsonObject();
            foreach (var stepExecution in execution.StepExecutions
                         .Where(se => se.Output != null)
                         .OrderBy(se => se.StepNumber))
            {
                outputsNode[stepExecution.StepNumber.ToString()] = TryParseAsJson(stepExecution.Output);
            }

            return new JsonObject
            {
                ["event"] = eventNode,
                ["output"] = outputsNode
            };
        }

        private static JsonNode? TryParseAsJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return JsonValue.Create(json);
            }
        }
    }
}