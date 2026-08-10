using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;
using SignalForge.Domain.ValueObjects;

namespace SignalForge.Api.Controllers;

/// <summary>
/// Controller for ingesting events with idempotency support.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class EventsController : ControllerBase
{
    private readonly IEventIngestionService _eventIngestionService;
    private readonly ILogger<EventsController> _logger;

    public EventsController(IEventIngestionService eventIngestionService, ILogger<EventsController> logger)
    {
        _eventIngestionService = eventIngestionService;
        _logger = logger;
    }

    /// <summary>
    /// Ingests an event with idempotency protection.
    /// If an event with the same TenantId and ExternalEventId already exists,
    /// returns the existing event without creating a duplicate.
    /// </summary>
    /// <param name="request">The event ingestion request</param>
    /// <returns>The ingested event (either newly created or existing)</returns>
    [HttpPost]
    [ProducesResponseType(typeof(EventDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(EventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<EventDto>> IngestEvent([FromBody] IngestEventRequest request)
    {
        try
        {
            // Get tenant ID from authenticated user
            var tenantIdClaim = User.FindFirst("tenant_id");
            if (tenantIdClaim == null || !Guid.TryParse(tenantIdClaim.Value, out var tenantId))
            {
                return Unauthorized(new ProblemDetails
                {
                    Title = "Invalid tenant information",
                    Status = StatusCodes.Status401Unauthorized,
                    Detail = "Unable to determine tenant from authentication token"
                });
            }

            // Validate request
            if (string.IsNullOrWhiteSpace(request.ExternalEventId))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "External event ID is required",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = "The external event ID (idempotency key) must be provided"
                });
            }

            if (string.IsNullOrWhiteSpace(request.EventType))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Event type is required",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = "The event type must be provided"
                });
            }

            if (request.Payload == null)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Event payload is required",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = "The event payload must be provided"
                });
            }

            // Ingest the event with idempotency
            var result = await _eventIngestionService.IngestEventAsync(
                tenantId,
                request.ExternalEventId,
                request.EventType,
                request.OccurredAt ?? DateTime.UtcNow,
                request.Payload);

            if (result.IsNewEvent)
            {
                _logger.LogInformation("New event ingested: {EventId} for tenant {TenantId}",
                    result.Event.Id, tenantId);
                return CreatedAtAction(nameof(GetEventById), new { id = result.Event.Id },
                    EventDto.FromDomain(result.Event));
            }
            else
            {
                _logger.LogInformation("Duplicate event ignored: {ExternalEventId} for tenant {TenantId}",
                    request.ExternalEventId, tenantId);
                return Ok(EventDto.FromDomain(result.Event));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ingesting event");
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal server error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = "An unexpected error occurred while ingesting the event"
            });
        }
    }

    /// <summary>
    /// Gets an event by its ID.
    /// </summary>
    /// <param name="id">The event ID</param>
    /// <returns>The event if found</returns>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(EventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<EventDto>> GetEventById(Guid id)
    {
        try
        {
            // Get tenant ID from authenticated user
            var tenantIdClaim = User.FindFirst("tenant_id");
            if (tenantIdClaim == null || !Guid.TryParse(tenantIdClaim.Value, out var tenantId))
            {
                return Unauthorized(new ProblemDetails
                {
                    Title = "Invalid tenant information",
                    Status = StatusCodes.Status401Unauthorized,
                    Detail = "Unable to determine tenant from authentication token"
                });
            }

            var eventEntity = await _eventIngestionService.GetEventByIdAsync(id, tenantId);
            if (eventEntity == null)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Event not found",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Event with ID {id} not found for this tenant"
                });
            }

            return Ok(EventDto.FromDomain(eventEntity));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving event {EventId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal server error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = "An unexpected error occurred while retrieving the event"
            });
        }
    }
}

/// <summary>
/// Request model for ingesting an event.
/// </summary>
public class IngestEventRequest
{
    /// <summary>
    /// The external event ID (idempotency key) from the sending system.
    /// </summary>
    public string ExternalEventId { get; set; } = default!;

    /// <summary>
    /// The type of event (e.g., "order.created", "payment.received").
    /// </summary>
    public string EventType { get; set; } = default!;

    /// <summary>
    /// When the event actually occurred (defaults to now if not provided).
    /// </summary>
    public DateTime? OccurredAt { get; set; }

    /// <summary>
    /// The event payload as a JSON string.
    /// </summary>
    public string Payload { get; set; } = default!;
}

/// <summary>
/// Response model for event data.
/// </summary>
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