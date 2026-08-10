using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Data;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;
using SignalForge.Domain.ValueObjects;

namespace SignalForge.Application.Services;

/// <summary>
/// Implementation of event ingestion service with idempotency support.
/// </summary>
public class EventIngestionService : IEventIngestionService
{
    private readonly ISignalForgeDbContext _dbContext;
    private readonly IOutboxPublisher _outboxPublisher;

    public EventIngestionService(ISignalForgeDbContext dbContext, IOutboxPublisher outboxPublisher)
    {
        _dbContext = dbContext;
        _outboxPublisher = outboxPublisher;
    }

    /// <inheritdoc />
    public async Task<IngestEventResult> IngestEventAsync(
        Guid tenantId,
        string externalEventId,
        string eventType,
        DateTime occurredAt,
        string payload,
        CancellationToken cancellationToken = default)
    {
        // Check if event already exists for this tenant and external event ID (idempotency)
        var existingEvent = await _dbContext.Events
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId &&
                e.ExternalEventId == externalEventId, cancellationToken);

        if (existingEvent != null)
        {
            // Return existing event (idempotency)
            return new IngestEventResult
            {
                Event = existingEvent,
                IsNewEvent = false
            };
        }

        // Create new event
        var newEvent = Event.Create(
            tenantId,
            externalEventId,
            eventType,
            occurredAt,
            payload);

        _dbContext.Events.Add(newEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new IngestEventResult
        {
            Event = newEvent,
            IsNewEvent = true
        };
    }

    /// <inheritdoc />
    public async Task PublishAsync(Guid tenantId, string eventType, string payload, CancellationToken cancellationToken = default)
    {
        await _outboxPublisher.PublishAsync(tenantId, eventType, payload, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Event?> GetEventByIdAsync(Guid eventId, Guid tenantId)
    {
        return await _dbContext.Events
            .FirstOrDefaultAsync(e =>
                e.Id == eventId &&
                e.TenantId == tenantId);
    }
}