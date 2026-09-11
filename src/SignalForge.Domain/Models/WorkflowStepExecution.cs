namespace SignalForge.Domain.Models;

/// <summary>
/// Represents the execution of a single step within a workflow execution.
/// Tracks the step's state, attempts, and results.
/// </summary>
public class WorkflowStepExecution
{
    public Guid Id { get; private set; }
    public Guid WorkflowExecutionId { get; private set; }
    public Guid WorkflowStepId { get; private set; }
    public int StepNumber { get; private set; } // Denormalized for querying
    public string Status { get; private set; } = default!; // Pending, Running, Succeeded, Failed, etc.
    public int AttemptNumber { get; private set; } // Current attempt number
    public int MaxAttempts { get; private set; } // Maximum attempts allowed
    public DateTime StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? NextRetryAt { get; private set; } // When to retry next
    public string? ErrorMessage { get; private set; } // Error details if failed
    public string? Output { get; private set; } // Step output/result
    public int? RouteToStepNumber { get; private set; } // Target step number when this step completes (branching), null = sequential next

    // Navigation properties
    public WorkflowExecution WorkflowExecution { get; private set; } = default!;
    public WorkflowStep WorkflowStep { get; private set; } = default!;

    private WorkflowStepExecution() { } // For EF Core

    private WorkflowStepExecution(
        Guid id,
        Guid workflowExecutionId,
        Guid workflowStepId,
        int stepNumber,
        int maxAttempts = 3)
    {
        Id = id;
        WorkflowExecutionId = workflowExecutionId;
        WorkflowStepId = workflowStepId;
        StepNumber = stepNumber;
        Status = WorkflowStepExecutionStatus.Pending;
        AttemptNumber = 0;
        MaxAttempts = maxAttempts;
        StartedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a new workflow step execution.
    /// </summary>
    /// <param name="workflowExecutionId">The workflow execution ID</param>
    /// <param name="workflowStepId">The workflow step ID</param>
    /// <param name="stepNumber">The step number</param>
    /// <param name="maxAttempts">Maximum attempts allowed</param>
    /// <returns>A new WorkflowStepExecution instance</returns>
    public static WorkflowStepExecution Create(
        Guid workflowExecutionId,
        Guid workflowStepId,
        int stepNumber,
        int maxAttempts = 3)
    {
        return new WorkflowStepExecution(
            Guid.NewGuid(),
            workflowExecutionId,
            workflowStepId,
            stepNumber,
            maxAttempts);
    }

    /// <summary>
    /// Starts executing the step.
    /// </summary>
    public void Start()
    {
        if (Status != WorkflowStepExecutionStatus.Pending)
            throw new InvalidOperationException($"Cannot start step execution in {Status} status");

        Status = WorkflowStepExecutionStatus.Running;
        AttemptNumber++;
        StartedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Marks the step execution as succeeded.
    /// </summary>
    /// <param name="output">Optional output/result from the step</param>
    /// <param name="routeToStepNumber">Optional target step number to route to next; null means sequential next step</param>
    public void Succeed(string? output = null, int? routeToStepNumber = null)
    {
        if (Status != WorkflowStepExecutionStatus.Running)
            throw new InvalidOperationException($"Cannot succeed step execution in {Status} status");

        Status = WorkflowStepExecutionStatus.Succeeded;
        CompletedAt = DateTime.UtcNow;
        Output = output;
        RouteToStepNumber = routeToStepNumber;
    }

    /// <summary>
    /// Marks the step execution as failed.
    /// </summary>
    /// <param name="errorMessage">Error message</param>
    public void Fail(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            throw new ArgumentException("Error message cannot be empty", nameof(errorMessage));

        if (Status != WorkflowStepExecutionStatus.Running)
            throw new InvalidOperationException($"Cannot fail step execution in {Status} status");

        Status = WorkflowStepExecutionStatus.Failed;
        CompletedAt = DateTime.UtcNow;
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Marks the step execution as cancelled.
    /// </summary>
    public void Cancel()
    {
        if (Status != WorkflowStepExecutionStatus.Running)
            throw new InvalidOperationException($"Cannot cancel step execution in {Status} status");

        Status = WorkflowStepExecutionStatus.Cancelled;
        CompletedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Marks the step execution as retrying (will be retried later).
    /// </summary>
    /// <param name="nextRetryAt">When to retry next</param>
    public void Retry(DateTime nextRetryAt)
    {
        if (Status != WorkflowStepExecutionStatus.Failed)
            throw new InvalidOperationException($"Can only retry failed step executions");

        if (AttemptNumber >= MaxAttempts)
            throw new InvalidOperationException($"Maximum attempts ({MaxAttempts}) exceeded");

        Status = WorkflowStepExecutionStatus.Retrying;
        NextRetryAt = nextRetryAt;
    }

    /// <summary>
    /// Prepares the step execution for a retry attempt. Accepts a step that is currently in
    /// <see cref="WorkflowStepExecutionStatus.Retrying"/> (scheduled by the engine's backoff) or a
    /// deliberately failed step that still has attempts left — the retry happens on the same
    /// record instead of minting a duplicate.
    /// </summary>
    public void PrepareForRetry()
    {
        if (Status != WorkflowStepExecutionStatus.Retrying &&
            Status != WorkflowStepExecutionStatus.Failed)
            throw new InvalidOperationException($"Can only prepare for retry from retrying or failed status");

        if (Status == WorkflowStepExecutionStatus.Failed && !CanRetry())
            throw new InvalidOperationException($"Maximum attempts ({MaxAttempts}) exceeded");

        Status = WorkflowStepExecutionStatus.Pending;
        NextRetryAt = null; // Cleared when the step actually starts
    }

    /// <summary>
    /// Checks if the step execution can be retried.
    /// </summary>
    /// <returns>True if can be retried, false otherwise</returns>
    public bool CanRetry()
    {
        return Status == WorkflowStepExecutionStatus.Failed && AttemptNumber < MaxAttempts;
    }

    /// <summary>
    /// Checks if the step execution is completed (success, failure, or cancellation).
    /// </summary>
    /// <returns>True if step execution is finished</returns>
    public bool IsCompleted()
    {
        return Status == WorkflowStepExecutionStatus.Succeeded ||
               Status == WorkflowStepExecutionStatus.Failed ||
               Status == WorkflowStepExecutionStatus.Cancelled;
    }

    /// <summary>
    /// Checks if the step execution is currently running.
    /// </summary>
    /// <returns>True if step execution is running</returns>
    public bool IsRunning()
    {
        return Status == WorkflowStepExecutionStatus.Running;
    }

    /// <summary>
    /// Checks if it's time to retry the step execution.
    /// </summary>
    /// <returns>True if it's time to retry</returns>
    public bool IsTimeToRetry()
    {
        return Status == WorkflowStepExecutionStatus.Retrying &&
               NextRetryAt.HasValue &&
               NextRetryAt.Value <= DateTime.UtcNow;
    }
}

/// <summary>
/// Static class containing workflow step execution status constants.
/// </summary>
public static class WorkflowStepExecutionStatus
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string Retrying = "Retrying";
}