using System.Threading;
using System.Threading.Tasks;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

public interface IEventIngestionService
{
    /// <summary>
    /// Idempotent: an event with the same tenant + external ID already existing is returned as-is
    /// instead of creating a duplicate.
    /// </summary>
    Task<IngestEventResult> IngestEventAsync(
        Guid tenantId,
        string externalEventId,
        string eventType,
        DateTime occurredAt,
        string payload,
        CancellationToken cancellationToken = default);

    Task PublishAsync(Guid tenantId, string eventType, string payload, CancellationToken cancellationToken = default);

    Task<Event?> GetEventByIdAsync(Guid eventId, Guid tenantId);
}

public class IngestEventResult
{
    public Event Event { get; set; } = default!;
    public bool IsNewEvent { get; set; }
}