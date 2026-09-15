using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SignalForge.Application.Data;
using SignalForge.Application.Security;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

/// <inheritdoc cref="IStepExecutionRetryPolicy"/>
public class StepExecutionRetryPolicy(
    ISignalForgeDbContext dbContext,
    ILogger<StepExecutionRetryPolicy> logger) : IStepExecutionRetryPolicy
{
    /// <inheritdoc />
    public async Task<StepFailureResolution> ResolveFailureAsync(
        WorkflowExecution execution,
        WorkflowStepExecution stepExecution,
        string stepType,
        CancellationToken cancellationToken = default)
    {
        if (stepExecution.CanRetry())
        {
            await ScheduleRetryAsync(stepExecution, cancellationToken);
            return StepFailureResolution.Retrying;
        }

        // Max attempts reached: create the dead letter and fail the execution permanently.
        dbContext.DeadLetterMessages.Add(
            DeadLetterMessage.CreateFromFailedStep(stepExecution, execution, stepType));

        var allStepsCompleted = execution.StepExecutions.All(se => se.IsCompleted());
        execution.Fail(allStepsCompleted
            ? "One or more steps failed after maximum retry attempts"
            : $"Step {stepExecution.StepNumber} failed after maximum retry attempts");

        logger.LogWarning(
            "Step {stepExecutionId} of execution {executionId} exhausted its attempts; " +
            "moved to dead letter and failing execution",
            stepExecution.Id, execution.Id);

        await dbContext.SaveChangesAsync(cancellationToken);
        return StepFailureResolution.DeadLettered;
    }

    /// <inheritdoc />
    public async Task ScheduleRetryAsync(
        WorkflowStepExecution stepExecution,
        CancellationToken cancellationToken = default)
    {
        var retryDelay = TimeSpan.FromSeconds(Math.Pow(2, stepExecution.AttemptNumber));
        stepExecution.Retry(DateTime.UtcNow.Add(retryDelay));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}