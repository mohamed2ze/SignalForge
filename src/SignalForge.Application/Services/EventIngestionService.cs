using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Data;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;
using SignalForge.Domain.ValueObjects;

namespace SignalForge.Application.Services;

public class EventIngestionService : IEventIngestionService
{
    private readonly ISignalForgeDbContext _dbContext;
    private readonly IOutboxPublisher _outboxPublisher;
    private readonly IUniqueViolationDetector _uniqueViolationDetector;

    public EventIngestionService(
        ISignalForgeDbContext dbContext,
        IOutboxPublisher outboxPublisher,
        IUniqueViolationDetector uniqueViolationDetector)
    {
        _dbContext = dbContext;
        _outboxPublisher = outboxPublisher;
        _uniqueViolationDetector = uniqueViolationDetector;
    }

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
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (_uniqueViolationDetector.IsUniqueViolation(ex))
        {
            // Two concurrent POSTs with the same (TenantId, ExternalEventId) both passed the
            // check above; the unique index let only one insert through. The whole batch was
            // rolled back at the DB, so unwind our in-memory insert and report the idempotent
            // outcome. (Remove on an Added entity detaches it; it will not be re-inserted by a
            // later SaveChanges in the same scope.)
            _dbContext.Events.Remove(newEvent);

            var winner = await _dbContext.Events
                .FirstOrDefaultAsync(e =>
                    e.TenantId == tenantId &&
                    e.ExternalEventId == externalEventId, cancellationToken);

            if (winner is null)
                throw; // Not the idempotency index — surface the original failure.

            return new IngestEventResult
            {
                Event = winner,
                IsNewEvent = false
            };
        }

        return new IngestEventResult
        {
            Event = newEvent,
            IsNewEvent = true
        };
    }

    public async Task PublishAsync(Guid tenantId, string eventType, string payload, CancellationToken cancellationToken = default)
    {
        await _outboxPublisher.PublishAsync(tenantId, eventType, payload, cancellationToken);
    }

    public async Task<Event?> GetEventByIdAsync(Guid eventId, Guid tenantId)
    {
        return await _dbContext.Events
            .FirstOrDefaultAsync(e =>
                e.Id == eventId &&
                e.TenantId == tenantId);
    }
}