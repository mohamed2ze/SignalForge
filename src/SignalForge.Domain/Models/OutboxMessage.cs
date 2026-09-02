namespace SignalForge.Domain.Models;

/// <summary>
/// Represents an outbox message for the transactional outbox pattern.
/// Ensures that messages are not lost when the application crashes between
/// saving state and sending messages.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; } // Denormalized for querying efficiency
    public string Type { get; private set; } = default!; // Message type (e.g., "WorkflowStepCompleted")
    public string Payload { get; private set; } = default!; // JSON payload
    public DateTime CreatedAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
    public DateTime? FailedAt { get; private set; }
    public DateTime? NextRetryAt { get; private set; } // Per-message retry gate: not re-polled before this time (Decision #26)
    public int AttemptCount { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool IsProcessed { get; private set; }
    public Guid? ReplaySourceDeadLetterId { get; private set; } // Set when a dead letter is replayed (Decision #25): the dead letter that produced this requeue

    private OutboxMessage() { } // For EF Core

    private OutboxMessage(
        Guid id,
        Guid tenantId,
        string type,
        string payload,
        Guid? replaySourceDeadLetterId = null)
    {
        Id = id;
        TenantId = tenantId;
        Type = type ?? throw new ArgumentNullException(nameof(type));
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        CreatedAt = DateTime.UtcNow;
        AttemptCount = 0;
        IsProcessed = false;
        ReplaySourceDeadLetterId = replaySourceDeadLetterId;
    }

    /// <summary>
    /// Creates a new outbox message.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="type">The message type</param>
    /// <param name="payload">The JSON payload</param>
    /// <returns>A new OutboxMessage instance</returns>
    public static OutboxMessage Create(Guid tenantId, string type, string payload)
    {
        return new OutboxMessage(
            Guid.NewGuid(),
            tenantId,
            type,
            payload);
    }

    /// <summary>
    /// Creates a new outbox message as a requeue of a dead letter (Decision #25): same
    /// tenant/type/payload as the original, tagged with the source dead letter so the in-flight
    /// requeue rule ("at most one unprocessed requeue per dead letter") and the provenance chain
    /// (outbox attempt → dead letter) can be tracked.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="type">The message type</param>
    /// <param name="payload">The JSON payload</param>
    /// <param name="sourceDeadLetterId">The dead letter this message is replayed from</param>
    /// <returns>A new OutboxMessage instance</returns>
    public static OutboxMessage CreateReplay(
        Guid tenantId,
        string type,
        string payload,
        Guid sourceDeadLetterId)
    {
        if (sourceDeadLetterId == Guid.Empty)
            throw new ArgumentException("Source dead letter ID cannot be empty", nameof(sourceDeadLetterId));

        return new OutboxMessage(
            Guid.NewGuid(),
            tenantId,
            type,
            payload,
            sourceDeadLetterId);
    }

    /// <summary>
    /// Marks the message as processed successfully.
    /// </summary>
    public void MarkAsProcessed()
    {
        IsProcessed = true;
        ProcessedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Marks the message as failed.
    /// </summary>
    /// <param name="errorMessage">The error message</param>
    public void MarkAsFailed(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            throw new ArgumentException("Error message cannot be empty", nameof(errorMessage));

        FailedAt = DateTime.UtcNow;
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Schedules when the message may be re-polled after a failure (Decision #26).
    /// The retry gate keeps a poison message from being re-claimed (and re-failed) on
    /// consecutive poll cycles, giving the underlying failure time to recover before
    /// the next attempt.
    /// </summary>
    /// <param name="nextRetryAtUtc">The earliest UTC time at which the message may be claimed again</param>
    public void ScheduleNextRetry(DateTime nextRetryAtUtc)
    {
        if (nextRetryAtUtc.Kind == DateTimeKind.Local)
            throw new ArgumentException("Scheduled retry time must be UTC", nameof(nextRetryAtUtc));

        if (!HasFailed())
            throw new InvalidOperationException("Cannot schedule a retry before the message has failed");

        NextRetryAt = nextRetryAtUtc;
    }

    /// <summary>
    /// Increments the attempt count.
    /// </summary>
    public void IncrementAttempt()
    {
        AttemptCount++;
    }

    /// <summary>
    /// Checks if the message has been processed.
    /// </summary>
    /// <returns>True if processed, false otherwise</returns>
    public bool IsProcessedSuccessfully()
    {
        return IsProcessed && ProcessedAt.HasValue;
    }

    /// <summary>
    /// Checks if the message has failed.
    /// </summary>
    /// <returns>True if failed, false otherwise</returns>
    public bool HasFailed()
    {
        return FailedAt.HasValue;
    }
}