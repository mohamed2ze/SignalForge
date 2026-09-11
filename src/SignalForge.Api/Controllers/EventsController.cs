using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SignalForge.Api.Dtos;
using SignalForge.Application.Services;
using static SignalForge.Api.Controllers.ApiControllerExtensions;

namespace SignalForge.Api.Controllers;

/// <summary>
/// Controller for ingesting events with idempotency support.
/// </summary>
[ApiController]
[Route("api/events")]
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
            // Resolve and validate tenant from the authenticated user
            if (!this.TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem());

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
            return this.InternalServerError("An unexpected error occurred while ingesting the event");
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
            // Resolve and validate tenant from the authenticated user
            if (!this.TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem());

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
            return this.InternalServerError("An unexpected error occurred while retrieving the event");
        }
    }
}
