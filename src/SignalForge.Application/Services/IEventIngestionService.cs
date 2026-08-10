using System.Threading;
using System.Threading.Tasks;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

/// <summary>
/// Service for ingesting events with idempotency support.
/// </summary>
public interface IEventIngestionService
{
    /// <summary>
    /// Ingests an event with idempotency protection.
    /// If an event with the same TenantId and ExternalEventId already exists,
    /// returns the existing event without creating a duplicate.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="externalEventId">The external event ID (idempotency key)</param>
    /// <param name="eventType">The type of event</param>
    /// <param name="occurredAt">When the event occurred</param>
    /// <param name="payload">The event payload as JSON</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result indicating whether a new event was created or existing one returned</returns>
    Task<IngestEventResult> IngestEventAsync(
        Guid tenantId,
        string externalEventId,
        string eventType,
        DateTime occurredAt,
        string payload,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an event to the outbox for later processing.
    /// </summary>
    /// <param name="tenantId">The tenant the event belongs to</param>
    /// <param name="eventType">The type of event</param>
    /// <param name="payload">The event payload as JSON</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task PublishAsync(Guid tenantId, string eventType, string payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an event by its ID for a specific tenant.
    /// </summary>
    /// <param name="eventId">The event ID</param>
    /// <param name="tenantId">The tenant ID</param>
    /// <returns>The event if found and belongs to the tenant, otherwise null</returns>
    Task<Event?> GetEventByIdAsync(Guid eventId, Guid tenantId);
}

/// <summary>
/// Result of event ingestion operation.
/// </summary>
public class IngestEventResult
{
    public Event Event { get; set; } = default!;
    public bool IsNewEvent { get; set; }
}