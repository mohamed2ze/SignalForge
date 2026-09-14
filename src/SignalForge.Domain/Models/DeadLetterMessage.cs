namespace SignalForge.Domain.Models;

/// <summary>
/// Represents a message that has failed processing and exceeded its retry limits.
/// These messages are moved to a dead letter queue for manual inspection and handling.
/// </summary>
public class DeadLetterMessage
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; } // Denormalized for querying efficiency
    public string OriginalMessageType { get; private set; } = default!; // Type of original message
    public string OriginalPayload { get; private set; } = default!; // Original payload
    public string FailedStepType { get; private set; } = default!; // Type of step that failed
    public int FailedStepNumber { get; private set; } // Step number that failed
    public Guid? WorkflowExecutionId { get; private set; } // ID of the workflow execution (null for outbox-originated dead letters)
    public Guid? WorkflowStepExecutionId { get; private set; } // ID of the step execution that failed (null for outbox-originated dead letters)
    public string ErrorMessage { get; private set; } = default!; // Final error message
    public int FinalAttemptCount { get; private set; } // Number of attempts made
    public DateTime CreatedAt { get; private set; } // When the dead letter was created
    public DateTime? ProcessedAt { get; private set; } // When it was manually processed (if ever)
    public bool IsProcessed { get; private set; } // Whether it has been manually processed
    public int ReplayCount { get; private set; } // Number of times replayed
    public DateTime? LastReplayedAt { get; private set; } // Most recent replay time
    public Guid? ReplayedFromDeadLetterId { get; private set; } // The dead letter this one was replayed from, if any

    // Navigation properties (optional, for querying)
    public WorkflowExecution? WorkflowExecution { get; private set; }
    public WorkflowStepExecution? WorkflowStepExecution { get; private set; }

    private DeadLetterMessage() { } // For EF Core

    private DeadLetterMessage(
        Guid id,
        Guid tenantId,
        string originalMessageType,
        string originalPayload,
        string failedStepType,
        int failedStepNumber,
        Guid? workflowExecutionId,
        Guid? workflowStepExecutionId,
        string errorMessage,
        int finalAttemptCount,
        Guid? replayedFromDeadLetterId = null)
    {
        Id = id;
        TenantId = tenantId;
        OriginalMessageType = originalMessageType ?? throw new ArgumentNullException(nameof(originalMessageType));
        OriginalPayload = originalPayload ?? throw new ArgumentNullException(nameof(originalPayload));
        FailedStepType = failedStepType ?? throw new ArgumentNullException(nameof(failedStepType));
        FailedStepNumber = failedStepNumber;
        WorkflowExecutionId = workflowExecutionId;
        WorkflowStepExecutionId = workflowStepExecutionId;
        ErrorMessage = errorMessage ?? throw new ArgumentNullException(nameof(errorMessage));
        FinalAttemptCount = finalAttemptCount;
        CreatedAt = DateTime.UtcNow;
        IsProcessed = false;
        ReplayCount = 0;
        ReplayedFromDeadLetterId = replayedFromDeadLetterId;
    }

    /// <summary>
    /// Creates a new dead letter message from a failed workflow step execution. The step type is
    /// read from the step execution's <see cref="WorkflowStep"/> navigation; when that navigation is
    /// not loaded (e.g. the step execution was loaded without its step), use the
    /// <see cref="CreateFromFailedStep(WorkflowStepExecution, WorkflowExecution, string)"/> overload
    /// to supply the type explicitly.
    /// </summary>
    /// <param name="workflowStepExecution">The failed step execution</param>
    /// <param name="workflowExecution">The workflow execution</param>
    /// <returns>A new DeadLetterMessage instance</returns>
    public static DeadLetterMessage CreateFromFailedStep(
        WorkflowStepExecution workflowStepExecution,
        WorkflowExecution workflowExecution)
    {
        ArgumentNullException.ThrowIfNull(workflowStepExecution);
        ArgumentNullException.ThrowIfNull(workflowExecution);

        return CreateFromFailedStep(
            workflowStepExecution,
            workflowExecution,
            workflowStepExecution.WorkflowStep?.StepType
                ?? throw new InvalidOperationException(
                    "Step execution does not have its WorkflowStep navigation loaded; " +
                    "pass the step type explicitly instead."));
    }

    /// <summary>
    /// Creates a new dead letter message from a failed workflow step execution, with the failing
    /// step's type supplied explicitly (no navigation requirement).
    /// </summary>
    /// <param name="workflowStepExecution">The failed step execution</param>
    /// <param name="workflowExecution">The workflow execution</param>
    /// <param name="failedStepType">The workflow step type that failed</param>
    /// <returns>A new DeadLetterMessage instance</returns>
    public static DeadLetterMessage CreateFromFailedStep(
        WorkflowStepExecution workflowStepExecution,
        WorkflowExecution workflowExecution,
        string failedStepType)
    {
        return new DeadLetterMessage(
            Guid.NewGuid(),
            workflowExecution.TenantId,
            failedStepType,
            workflowStepExecution.Output ?? "{}", // Store the last output/payload
            failedStepType,
            workflowStepExecution.StepNumber,
            workflowExecution.Id,
            workflowStepExecution.Id,
            workflowStepExecution.ErrorMessage ?? "Unknown error",
            workflowStepExecution.AttemptNumber);
    }

    /// <summary>
    /// Creates a new dead letter message from an outbox message that exceeded its retry limits.
    /// Not tied to a workflow step, so the step reference fields are null. When the outbox message
    /// is itself a dead-letter replay, the provenance chain is preserved on
    /// <see cref="ReplayedFromDeadLetterId"/> so the two occurrences stay distinguishable.
    /// </summary>
    /// <param name="outboxMessage">The failed outbox message</param>
    /// <param name="errorMessage">The final error message</param>
    /// <returns>A new DeadLetterMessage instance</returns>
    public static DeadLetterMessage CreateFromOutboxMessage(
        OutboxMessage outboxMessage,
        string errorMessage)
    {
        return new DeadLetterMessage(
            Guid.NewGuid(),
            outboxMessage.TenantId,
            outboxMessage.Type,
            outboxMessage.Payload,
            "Outbox",
            0,
            null,
            null,
            errorMessage,
            outboxMessage.AttemptCount,
            outboxMessage.ReplaySourceDeadLetterId);
    }

    /// <summary>
    /// Records a replay of this dead letter: increments the replay counter and
    /// stamps the last replay time so repeated replays are observable.
    /// </summary>
    public void RecordReplay()
    {
        ReplayCount++;
        LastReplayedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Compensates a replay attempt: called when the replay's outbox insert is
    /// rejected by the in-flight unique index, restoring the counters to the values captured
    /// before <see cref="RecordReplay"/> so the losing replica's in-memory state matches the DB
    /// (the whole replay transaction was rolled back, the counter changes never persisted).
    /// </summary>
    public void RollBackReplay(int previousReplayCount, DateTime? previousLastReplayedAt)
    {
        ReplayCount = previousReplayCount;
        LastReplayedAt = previousLastReplayedAt;
    }

    /// <summary>
    /// Marks the dead letter message as processed (manually handled).
    /// </summary>
    public void MarkAsProcessed()
    {
        IsProcessed = true;
        ProcessedAt = DateTime.UtcNow;
    }
}