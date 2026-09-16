namespace SignalForge.Domain.Models;

/// <summary>
/// Represents an incoming event that triggers workflows.
/// Events have stable identities to support idempotency.
/// </summary>
public class Event
{
    /// <summary>
    /// Upper bound for the event payload length in characters. Enforced at the write boundary
    /// (domain <see cref="Create"/>), by the ingest request validation, and declared on the
    /// persisted column via the EF mapping.
    /// </summary>
    public const int PayloadMaxLength = 1_048_576;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string ExternalEventId { get; private set; } = default!; // Idempotency key from external system
    public string EventType { get; private set; } = default!; // Type of event (e.g., "order.created")
    public DateTime OccurredAt { get; private set; } // When the event actually occurred
    public string Payload { get; private set; } = default!; // JSON payload
    public DateTime ReceivedAt { get; private set; } // When we received the event
    public DateTime? ProcessedAt { get; private set; } // When the event was fully processed
    public bool IsProcessed { get; private set; }

    // Navigation properties
    public Tenant Tenant { get; private set; } = default!;
    public ICollection<WorkflowExecution> WorkflowExecutions { get; private set; } = new List<WorkflowExecution>();

    private Event() { } // For EF Core

    private Event(
        Guid id,
        Guid tenantId,
        string externalEventId,
        string eventType,
        DateTime occurredAt,
        string payload)
    {
        Id = id;
        TenantId = tenantId;
        ExternalEventId = externalEventId ?? throw new ArgumentNullException(nameof(externalEventId));
        EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
        OccurredAt = occurredAt;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        ReceivedAt = DateTime.UtcNow;
        IsProcessed = false;
    }

    /// <summary>
    /// Creates a new event.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="externalEventId">The external event ID (idempotency key)</param>
    /// <param name="eventType">The type of event</param>
    /// <param name="occurredAt">When the event occurred</param>
    /// <param name="payload">The event payload as JSON</param>
    /// <returns>A new Event instance</returns>
    public static Event Create(
        Guid tenantId,
        string externalEventId,
        string eventType,
        DateTime occurredAt,
        string payload)
    {
        if (string.IsNullOrWhiteSpace(externalEventId))
            throw new ArgumentException("External event ID cannot be empty", nameof(externalEventId));

        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("Event type cannot be empty", nameof(eventType));

        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("Event payload cannot be empty", nameof(payload));

        if (payload.Length > PayloadMaxLength)
            throw new ArgumentException($"Event payload exceeds the maximum length of {PayloadMaxLength} characters", nameof(payload));

        return new Event(
            Guid.NewGuid(),
            tenantId,
            externalEventId.Trim(),
            eventType.Trim(),
            occurredAt.ToUniversalTime(), // Ensure UTC
            payload
        );
    }

    /// <summary>
    /// Marks the event as processed.
    /// </summary>
    public void MarkAsProcessed()
    {
        IsProcessed = true;
        ProcessedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Checks if this event is a duplicate based on tenant and external event ID.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="externalEventId">The external event ID</param>
    /// <returns>True if this represents the same event</returns>
    public bool IsSameEvent(Guid tenantId, string externalEventId)
    {
        return TenantId == tenantId && ExternalEventId == externalEventId;
    }
}