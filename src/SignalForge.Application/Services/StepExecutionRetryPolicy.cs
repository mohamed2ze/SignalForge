using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalForge.Application.Data;
using SignalForge.Application.Security;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

public class StepExecutionRetryPolicy(
    ISignalForgeDbContext dbContext,
    IOptions<StepRetryPolicyOptions> options,
    ILogger<StepExecutionRetryPolicy> logger) : IStepExecutionRetryPolicy
{
    private readonly StepRetryPolicyOptions _options = options.Value;

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

    public async Task ScheduleRetryAsync(
        WorkflowStepExecution stepExecution,
        CancellationToken cancellationToken = default)
    {
        // Exponential backoff with a cap and jitter: 2^attempt seconds, capped at
        // MaxBackoffSeconds, then randomized to ±50% (factor in [0.5, 1.0)) so a burst of failed
        // steps does not all retry on the same clock tick. The cap keeps a repeatedly failing
        // step from pushing its resumption arbitrarily far into the future.
        var baseSeconds = Math.Min(Math.Pow(2, stepExecution.AttemptNumber), _options.MaxBackoffSeconds);
        var jitteredSeconds = baseSeconds * (0.5 + Random.Shared.NextDouble() * 0.5);
        var retryDelay = TimeSpan.FromSeconds(jitteredSeconds);
        stepExecution.Retry(DateTime.UtcNow.Add(retryDelay));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}