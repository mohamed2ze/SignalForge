using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;

namespace SignalForge.Api.Controllers;

/// <summary>
/// Controller for managing dead letter messages.
/// </summary>
[ApiController]
[Route("api/[controller]")]
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
    /// Gets dead letter messages for the current tenant with optional filtering and paging
    /// (Level 5 / Decision #25): workflow (via step-execution provenance), cause substring on the
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
    [ProducesResponseType(typeof(PagedDeadLettersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedDeadLettersResponse>> GetDeadLetters(
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
            if (!TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem);

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

            return Ok(new PagedDeadLettersResponse
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
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal server error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = "An unexpected error occurred while retrieving dead letters"
            });
        }
    }

    /// <summary>
    /// Replays a dead letter (Decision #25): requeues the message through the outbox for a fresh
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
            if (!TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem);

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
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal server error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = "An unexpected error occurred while replaying the dead letter"
            });
        }
    }

    /// <summary>
    /// Gets a dead letter message by its ID.
    /// </summary>
    /// <param name="id">The dead letter message ID</param>
    /// <returns>The dead letter message if found</returns>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DeadLetterDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DeadLetterDto>> GetDeadLetterById(Guid id)
    {
        try
        {
            if (!TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem);

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
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal server error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = "An unexpected error occurred while retrieving the dead letter"
            });
        }
    }

    /// <summary>
    /// Marks a dead letter message as processed (manually handled).
    /// </summary>
    /// <param name="id">The dead letter message ID</param>
    /// <returns>True if the dead letter was found and marked as processed, false otherwise</returns>
    [HttpPost("{id:guid}/process")]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<bool>> MarkAsProcessed(Guid id)
    {
        try
        {
            if (!TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem);

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
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal server error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = "An unexpected error occurred while marking the dead letter as processed"
            });
        }
    }

    /// <summary>
    /// Gets counts of dead letter messages for the current tenant.
    /// </summary>
    /// <returns>Dead letter counts</returns>
    [HttpGet("counts")]
    [ProducesResponseType(typeof(DeadLetterCountsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<DeadLetterCountsDto>> GetDeadLetterCounts()
    {
        try
        {
            if (!TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem);

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
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal server error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = "An unexpected error occurred while retrieving dead letter counts"
            });
        }
    }

    private bool TryGetTenantId(out Guid tenantId)
    {
        var tenantIdClaim = User.FindFirst("tenant_id");
        if (tenantIdClaim == null || !Guid.TryParse(tenantIdClaim.Value, out tenantId))
        {
            tenantId = Guid.Empty;
            return false;
        }
        return true;
    }

    private ProblemDetails TenantProblem => new ProblemDetails
    {
        Title = "Invalid tenant information",
        Status = StatusCodes.Status401Unauthorized,
        Detail = "Unable to determine tenant from authentication token"
    };

    // from/to are optional ISO 8601 instants; null means "no bound". Invalid non-null values
    // parse to false so the caller can reject the request.
    private static bool TryParseUtcInstant(string? value, out DateTime? utc)
    {
        utc = null;
        if (value == null)
            return true;

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            utc = parsed.UtcDateTime;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Response model for dead letter message data.
/// </summary>
public class DeadLetterDto
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string OriginalMessageType { get; set; } = default!;
    public string OriginalPayload { get; set; } = default!;
    public string FailedStepType { get; set; } = default!;
    public int FailedStepNumber { get; set; }
    public Guid? WorkflowExecutionId { get; set; }
    public Guid? WorkflowStepExecutionId { get; set; }
    public string ErrorMessage { get; set; } = default!;
    public int FinalAttemptCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public bool IsProcessed { get; set; }
    public int ReplayCount { get; set; }
    public DateTime? LastReplayedAt { get; set; }
    public Guid? ReplayedFromDeadLetterId { get; set; }

    public static DeadLetterDto FromDomain(DeadLetterMessage deadLetter)
    {
        return new DeadLetterDto
        {
            Id = deadLetter.Id,
            TenantId = deadLetter.TenantId,
            OriginalMessageType = deadLetter.OriginalMessageType,
            OriginalPayload = deadLetter.OriginalPayload,
            FailedStepType = deadLetter.FailedStepType,
            FailedStepNumber = deadLetter.FailedStepNumber,
            WorkflowExecutionId = deadLetter.WorkflowExecutionId,
            WorkflowStepExecutionId = deadLetter.WorkflowStepExecutionId,
            ErrorMessage = deadLetter.ErrorMessage,
            FinalAttemptCount = deadLetter.FinalAttemptCount,
            CreatedAt = deadLetter.CreatedAt,
            ProcessedAt = deadLetter.ProcessedAt,
            IsProcessed = deadLetter.IsProcessed,
            ReplayCount = deadLetter.ReplayCount,
            LastReplayedAt = deadLetter.LastReplayedAt,
            ReplayedFromDeadLetterId = deadLetter.ReplayedFromDeadLetterId
        };
    }
}

/// <summary>
/// Paged envelope for the Level 5 dead-letter list (Decision #25): items plus the applied
/// page/pageSize and the exact total matching the filters so clients can page without guessing.
/// </summary>
public class PagedDeadLettersResponse
{
    public List<DeadLetterDto> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
}

/// <summary>
/// Response model for dead letter counts.
/// </summary>
public class DeadLetterCountsDto
{
    public int Total { get; set; }
    public int Unprocessed { get; set; }
    public int Processed { get; set; }
}