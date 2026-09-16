using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SignalForge.Application.Data;
using SignalForge.Application.Security;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

/// <inheritdoc cref="IWorkflowExecutionAdvancer"/>
public class WorkflowExecutionAdvancer : IWorkflowExecutionAdvancer
{
    private readonly ISignalForgeDbContext _dbContext;
    private readonly IStepProcessorRegistry _stepProcessors;
    private readonly IStepExecutionRetryPolicy _retryPolicy;
    private readonly ILogger<WorkflowExecutionAdvancer> _logger;

    public WorkflowExecutionAdvancer(
        ISignalForgeDbContext dbContext,
        IStepProcessorRegistry stepProcessors,
        IStepExecutionRetryPolicy retryPolicy,
        ILogger<WorkflowExecutionAdvancer> logger)
    {
        _dbContext = dbContext;
        _stepProcessors = stepProcessors;
        _retryPolicy = retryPolicy;
        _logger = logger;
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
        // - Running (fresh), or a retry that isn't due yet → busy, nothing to do. A Running step
        //   left behind by a crashed worker is requeued to Pending by the pump (which only selects
        //   executions whose next step is stale), so a live Running step never reaches here.
        // - Succeeded/Cancelled → the execution has been routed back onto an already-finished step
        //   (e.g. a conditional branch looping onto a past step); it can never progress, so
        //   dead-letter it once and fail the execution (previously PrepareForRetry threw on a
        //   terminal status here and the pump re-released the lease forever — a poison execution).
        // - Failed with attempts remaining, or Retrying and due → retry in place (no duplicates).
        // - Failed with attempts exhausted → fail the execution.
        // - Pending → the crash-window record a stale worker created but never got to start; the
        //   pump admitted it through the leaseWatermark guard, so run it in place (no duplicates).
        var existing = execution.StepExecutions
            .FirstOrDefault(se => se.StepNumber == nextStepNumber);

        WorkflowStepExecution stepExecution;
        if (existing != null)
        {
            if (existing.Status == WorkflowStepExecutionStatus.Succeeded ||
                existing.Status == WorkflowStepExecutionStatus.Cancelled)
            {
                _logger.LogWarning(
                    "Execution {executionId} routed onto already-completed step {stepNumber} " +
                    "({stepStatus}); failing execution and dead-lettering it",
                    execution.Id, existing.StepNumber, existing.Status);

                execution.Fail(
                    $"Step {existing.StepNumber} is already completed ({existing.Status}) but was routed onto again");
                _dbContext.DeadLetterMessages.Add(
                    DeadLetterMessage.CreateFromFailedStep(existing, execution, nextStep.StepType));
                await _dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }

            if (existing.Status == WorkflowStepExecutionStatus.Running ||
                (existing.Status == WorkflowStepExecutionStatus.Retrying &&
                 !existing.IsTimeToRetry()))
            {
                return false; // Busy or waiting for the retry window — skip this cycle
            }

            if (existing.Status == WorkflowStepExecutionStatus.Failed &&
                !existing.CanRetry())
            {
                // Exhausted all attempts for this step, fail the execution and dead-letter it.
                // (The step-retry policy normally dead-letters in the processor catch path; this
                // backstop covers re-entry after the failure was already persisted.)
                execution.Fail(
                    $"Step {existing.StepNumber} failed after maximum retry attempts");
                _dbContext.DeadLetterMessages.Add(
                    DeadLetterMessage.CreateFromFailedStep(existing, execution, nextStep.StepType));
                await _dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }

            if (existing.Status == WorkflowStepExecutionStatus.Pending)
            {
                stepExecution = existing; // Stale crash-window record: start it in place
            }
            else
            {
                existing.PrepareForRetry();
                stepExecution = existing;
            }
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
            var resolution = await _retryPolicy.ResolveFailureAsync(
                execution, stepExecution, nextStep.StepType, cancellationToken);

            // True when the step was rescheduled: the execution is still progressing, just on a
            // later cycle. False once the step is permanently dead-lettered.
            return resolution == StepFailureResolution.Retrying;
        }

        return true;
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