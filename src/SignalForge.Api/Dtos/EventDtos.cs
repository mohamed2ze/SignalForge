using SignalForge.Domain.Models;

namespace SignalForge.Api.Dtos;

public class IngestEventRequest
{
    /// <summary>
    /// The external event ID (idempotency key) from the sending system.
    /// </summary>
    public string ExternalEventId { get; set; } = default!;

    public string EventType { get; set; } = default!;

    public DateTime? OccurredAt { get; set; }

    public string Payload { get; set; } = default!;
}

public class EventDto
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string ExternalEventId { get; set; } = default!;
    public string EventType { get; set; } = default!;
    public DateTime OccurredAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public bool IsProcessed { get; set; }
    public string Payload { get; set; } = default!;

    public static EventDto FromDomain(Event @event)
    {
        return new EventDto
        {
            Id = @event.Id,
            TenantId = @event.TenantId,
            ExternalEventId = @event.ExternalEventId,
            EventType = @event.EventType,
            OccurredAt = @event.OccurredAt,
            ReceivedAt = @event.ReceivedAt,
            ProcessedAt = @event.ProcessedAt,
            IsProcessed = @event.IsProcessed,
            Payload = @event.Payload
        };
    }
}