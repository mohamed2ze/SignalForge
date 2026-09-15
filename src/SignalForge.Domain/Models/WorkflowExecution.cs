namespace SignalForge.Domain.Models;

/// <summary>
/// Represents an execution of a workflow version triggered by an event.
/// Tracks the execution state and progress through steps.
/// </summary>
public class WorkflowExecution
{
    public Guid Id { get; private set; }
    public Guid WorkflowVersionId { get; private set; }
    public Guid WorkflowId { get; private set; } // Denormalized for querying efficiency
    public Guid EventId { get; private set; }
    public Guid TenantId { get; private set; } // Denormalized for querying efficiency
    public string Status { get; private set; } = default!; // Pending, Running, Succeeded, Failed, etc.
    public int CurrentStepNumber { get; private set; } // Currently executing step (0 = not started)
    public DateTime StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public int RetryCount { get; private set; } // Number of retries attempted
    public string? ErrorMessage { get; private set; } // Error details if failed

    /// <summary>
    /// UTC instant at which a worker leased this execution for advancement. A row claimed by a
    /// live worker is invisible to every other worker for
    /// <c>ExecutionPumpOptions.ClaimLeaseSeconds</c>; a crashed worker's claim then expires and
    /// the execution becomes claimable again, giving the pump at-least-once (never exactly-once)
    /// progress across restarts and multiple worker instances.
    /// </summary>
    public DateTime? ClaimedAt { get; private set; }

    // Navigation properties
    public Workflow Workflow { get; private set; } = default!;
    public WorkflowVersion WorkflowVersion { get; private set; } = default!;
    public Event Event { get; private set; } = default!;
    public ICollection<WorkflowStepExecution> StepExecutions { get; private set; } = new List<WorkflowStepExecution>();

    private WorkflowExecution() { } // For EF Core

    private WorkflowExecution(
        Guid id,
        Guid workflowId,
        Guid workflowVersionId,
        Guid eventId,
        Guid tenantId)
    {
        Id = id;
        WorkflowId = workflowId;
        WorkflowVersionId = workflowVersionId;
        EventId = eventId;
        TenantId = tenantId;
        Status = WorkflowExecutionStatus.Pending;
        CurrentStepNumber = 0;
        StartedAt = DateTime.UtcNow;
        RetryCount = 0;
    }

    /// <summary>
    /// Creates a new workflow execution.
    /// </summary>
    /// <param name="workflowId">The workflow ID</param>
    /// <param name="workflowVersionId">The workflow version ID</param>
    /// <param name="eventId">The event ID that triggered this execution</param>
    /// <param name="tenantId">The tenant ID</param>
    /// <returns>A new WorkflowExecution instance</returns>
    public static WorkflowExecution Create(
        Guid workflowId,
        Guid workflowVersionId,
        Guid eventId,
        Guid tenantId)
    {
        return new WorkflowExecution(
            Guid.NewGuid(),
            workflowId,
            workflowVersionId,
            eventId,
            tenantId);
    }

    /// <summary>
    /// Starts the workflow execution.
    /// </summary>
    public void Start()
    {
        if (Status != WorkflowExecutionStatus.Pending)
            throw new InvalidOperationException($"Cannot start execution in {Status} status");

        Status = WorkflowExecutionStatus.Running;
    }

    /// <summary>
    /// Marks the execution as succeeded.
    /// </summary>
    public void Succeed()
    {
        if (Status != WorkflowExecutionStatus.Running)
            throw new InvalidOperationException($"Cannot succeed execution in {Status} status");

        Status = WorkflowExecutionStatus.Succeeded;
        CompletedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Marks the execution as failed.
    /// </summary>
    /// <param name="errorMessage">Optional error message</param>
    public void Fail(string? errorMessage = null)
    {
        if (Status != WorkflowExecutionStatus.Running)
            throw new InvalidOperationException($"Cannot fail execution in {Status} status");

        Status = WorkflowExecutionStatus.Failed;
        CompletedAt = DateTime.UtcNow;
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Marks the execution as cancelled.
    /// </summary>
    public void Cancel()
    {
        if (Status != WorkflowExecutionStatus.Running)
            throw new InvalidOperationException($"Cannot cancel execution in {Status} status");

        Status = WorkflowExecutionStatus.Cancelled;
        CompletedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Increments the retry count.
    /// </summary>
    public void IncrementRetry()
    {
        RetryCount++;
    }

    /// <summary>
    /// Advances to the next step.
    /// </summary>
    public void AdvanceStep()
    {
        if (Status != WorkflowExecutionStatus.Running)
            throw new InvalidOperationException($"Cannot advance step in {Status} status");

        CurrentStepNumber++;
    }

    /// <summary>
    /// Routes to a specific step number (used when a conditional step branches to then/else).
    /// </summary>
    /// <param name="stepNumber">The absolute step number to route to</param>
    public void RouteToStep(int stepNumber)
    {
        if (Status != WorkflowExecutionStatus.Running)
            throw new InvalidOperationException($"Cannot route step in {Status} status");

        if (stepNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(stepNumber), "Step number must be positive");

        CurrentStepNumber = stepNumber;
    }

    /// <summary>
    /// Resets the execution to pending state (for retry).
    /// </summary>
    public void ResetForRetry()
    {
        if (Status != WorkflowExecutionStatus.Failed && Status != WorkflowExecutionStatus.Cancelled)
            throw new InvalidOperationException($"Can only reset failed or cancelled executions");

        Status = WorkflowExecutionStatus.Pending;
        CurrentStepNumber = 0;
        CompletedAt = null;
        ErrorMessage = null;
    }

    /// <summary>
    /// Checks if the execution is completed (success, failure, or cancellation).
    /// </summary>
    /// <returns>True if execution is finished</returns>
    public bool IsCompleted()
    {
        return Status == WorkflowExecutionStatus.Succeeded ||
               Status == WorkflowExecutionStatus.Failed ||
               Status == WorkflowExecutionStatus.Cancelled;
    }

    /// <summary>
    /// Checks if the execution is currently running.
    /// </summary>
    /// <returns>True if execution is running</returns>
    public bool IsRunning()
    {
        return Status == WorkflowExecutionStatus.Running;
    }

    /// <summary>
    /// Leases this execution to the claiming worker by stamping <see cref="ClaimedAt"/>.
    /// </summary>
    /// <param name="claimedAtUtc">The UTC instant of the claim.</param>
    public void Claim(DateTime claimedAtUtc)
    {
        if (claimedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Claim time must be UTC", nameof(claimedAtUtc));

        ClaimedAt = claimedAtUtc;
    }

    /// <summary>
    /// Releases the lease after the worker has advanced (or failed to advance) the execution so
    /// another worker may pick it up without waiting out the full lease window.
    /// </summary>
    public void ReleaseClaim()
    {
        ClaimedAt = null;
    }
}

/// <summary>
/// Static class containing workflow execution status constants.
/// </summary>
public static class WorkflowExecutionStatus
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string Retrying = "Retrying";
}