using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SignalForge.Api.Dtos;
using SignalForge.Application.Services;
using static SignalForge.Api.Controllers.ApiControllerExtensions;

namespace SignalForge.Api.Controllers;

[ApiController]
[Route("api/dead-letters")]
[Produces("application/json")]
public class DeadLetterController : ControllerBase
{
    private readonly IDeadLetterProcessingService _deadLetterService;
    private readonly ILogger<DeadLetterController> _logger;

    public DeadLetterController(
        IDeadLetterProcessingService deadLetterService,
        ILogger<DeadLetterController> logger)
    {
        _deadLetterService = deadLetterService;
        _logger = logger;
    }

    /// <summary>
    /// Gets dead letter messages for the current tenant with optional filtering and paging:
    /// workflow (via step-execution provenance), cause substring on the
    /// final error message, processed-state, and a CreatedAt time window (ISO 8601).
    /// </summary>
    /// <param name="workflowId">Optional workflow to scope results to</param>
    /// <param name="cause">Optional substring filter on the final error message</param>
    /// <param name="onlyUnprocessed">If true, returns only unprocessed dead letters</param>
    /// <param name="from">Optional window start (ISO 8601) on CreatedAt</param>
    /// <param name="to">Optional window end (ISO 8601) on CreatedAt</param>
    /// <param name="page">1-based page number</param>
    /// <param name="pageSize">Page size (1..100)</param>
    /// <returns>A paged tile of dead letters</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PagedDeadLettersDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedDeadLettersDto>> GetDeadLetters(
        [FromQuery] Guid? workflowId,
        [FromQuery] string? cause,
        [FromQuery] bool onlyUnprocessed = false,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        try
        {
            if (!this.TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem());

            if (!TryParseUtcInstant(from, out var fromUtc) ||
                !TryParseUtcInstant(to, out var toUtc))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid time window",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = "The from/to window parameters must be ISO 8601 timestamps"
                });
            }

            var filter = new DeadLetterQueryFilter(
                workflowId,
                cause,
                onlyUnprocessed,
                fromUtc,
                toUtc,
                page,
                pageSize);

            PagedDeadLettersResult result;
            try
            {
                result = await _deadLetterService.GetDeadLettersPagedAsync(tenantId, filter);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid paging parameters",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = ex.Message
                });
            }

            return Ok(new PagedDeadLettersDto
            {
                Items = result.Items.Select(DeadLetterDto.FromDomain).ToList(),
                Page = result.Page,
                PageSize = result.PageSize,
                TotalCount = result.TotalCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving dead letters");
            return this.InternalServerError("An unexpected error occurred while retrieving dead letters");
        }
    }

    /// <summary>
    /// Replays a dead letter: requeues the message through the outbox for a fresh
    /// delivery attempt. Idempotent — a second replay while the requeue is still in flight returns
    /// 409 Conflict instead of duplicating the requeue. The dead letter stays visible and records
    /// the replay (count + timestamp); a terminal re-failure lands as a new dead letter linked back
    /// via <c>ReplayedFromDeadLetterId</c>.
    /// </summary>
    /// <param name="id">The dead letter message ID</param>
    /// <returns>The replay outcome and updated dead letter</returns>
    [HttpPost("{id:guid}/replay")]
    [ProducesResponseType(typeof(DeadLetterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeadLetterDto>> ReplayDeadLetter(Guid id)
    {
        try
        {
            if (!this.TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem());

            var result = await _deadLetterService.ReplayAsync(tenantId, id);

            return result.Status switch
            {
                DeadLetterReplayStatus.NotFound => NotFound(new ProblemDetails
                {
                    Title = "Dead letter not found",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Dead letter with ID {id} not found for this tenant"
                }),
                DeadLetterReplayStatus.AlreadyInFlight => Conflict(new ProblemDetails
                {
                    Title = "Replay already in flight",
                    Status = StatusCodes.Status409Conflict,
                    Detail = $"Dead letter {id} already has an unprocessed requeue; " +
                             "no duplicate requeue was created"
                }),
                _ => Ok(DeadLetterDto.FromDomain(result.DeadLetter!))
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error replaying dead letter {DeadLetterId}", id);
            return this.InternalServerError("An unexpected error occurred while replaying the dead letter");
        }
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DeadLetterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DeadLetterDto>> GetDeadLetterById(Guid id)
    {
        try
        {
            if (!this.TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem());

            var deadLetter = await _deadLetterService.GetDeadLetterByIdAsync(id, tenantId);
            if (deadLetter == null)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Dead letter not found",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Dead letter with ID {id} not found for this tenant"
                });
            }

            return Ok(DeadLetterDto.FromDomain(deadLetter));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving dead letter {DeadLetterId}", id);
            return this.InternalServerError("An unexpected error occurred while retrieving the dead letter");
        }
    }

    [HttpPost("{id:guid}/process")]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<bool>> MarkAsProcessed(Guid id)
    {
        try
        {
            if (!this.TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem());

            var result = await _deadLetterService.MarkAsProcessedAsync(id, tenantId);
            if (!result)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Dead letter not found",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Dead letter with ID {id} not found for this tenant"
                });
            }

            _logger.LogInformation("Dead letter {DeadLetterId} marked as processed", id);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking dead letter {DeadLetterId} as processed", id);
            return this.InternalServerError("An unexpected error occurred while marking the dead letter as processed");
        }
    }

    [HttpGet("counts")]
    [ProducesResponseType(typeof(DeadLetterCountsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DeadLetterCountsDto>> GetDeadLetterCounts()
    {
        try
        {
            if (!this.TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem());

            var counts = await _deadLetterService.GetDeadLetterCountsAsync(tenantId);
            return Ok(new DeadLetterCountsDto
            {
                Total = counts.Total,
                Unprocessed = counts.Unprocessed,
                Processed = counts.Processed
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving dead letter counts");
            return this.InternalServerError("An unexpected error occurred while retrieving dead letter counts");
        }
    }
}
