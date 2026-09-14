using Microsoft.AspNetCore.Mvc;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;

using static SignalForge.Api.Controllers.ApiControllerExtensions;

namespace SignalForge.Api.Controllers;

/// <summary>
/// Execution observability endpoints: server-side, tenant-scoped aggregates and paged history
/// for a future dashboard. All queries are scoped to the caller's tenant via the <c>tenant_id</c>
/// claim and the denormalized, untouched execution tenant column. GET-only; the signing surface
/// (POST /api/events) is unaffected by this controller.
/// </summary>
[ApiController]
[Route("api/executions")]
public sealed class ExecutionsController : ControllerBase
{
    private static readonly string[] AllowedStatuses =
    [
        WorkflowExecutionStatus.Pending,
        WorkflowExecutionStatus.Running,
        WorkflowExecutionStatus.Succeeded,
        WorkflowExecutionStatus.Failed,
        WorkflowExecutionStatus.Cancelled,
        WorkflowExecutionStatus.Retrying
    ];

    private readonly IExecutionObservabilityService _observability;
    private readonly Microsoft.Extensions.Logging.ILogger<ExecutionsController> _logger;

    public ExecutionsController(
        IExecutionObservabilityService observability,
        Microsoft.Extensions.Logging.ILogger<ExecutionsController> logger)
    {
        _observability = observability;
        _logger = logger;
    }

    /// <summary>
    /// Paged, filtered execution history for the caller's tenant.
    /// </summary>
    /// <param name="workflowId">Optional exact workflow filter.</param>
    /// <param name="status">Optional execution status filter.</param>
    /// <param name="from">Optional window start (ISO 8601, UTC).</param>
    /// <param name="to">Optional window end (ISO 8601, UTC).</param>
    /// <param name="page">1-based page number (default 1).</param>
    /// <param name="pageSize">Page size 1..100 (default 20).</param>
    [HttpGet]
    [ProducesResponseType(typeof(PagedExecutionsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedExecutionsResult>> GetExecutions(
        [FromQuery] Guid? workflowId,
        [FromQuery] string? status,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        if (!this.TryGetTenantId(out var tenantId))
            return Unauthorized();

        var normalizedStatus = status?.Trim();
        if (normalizedStatus is not null &&
            !AllowedStatuses.Contains(normalizedStatus, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid status filter",
                Status = StatusCodes.Status400BadRequest,
                Detail = $"Status must be one of: {string.Join(", ", AllowedStatuses)}"
            });
        }

        if (!TryParseWindow(from, to, out var fromUtc, out var toUtc))
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid time window",
                Status = StatusCodes.Status400BadRequest,
                Detail = "from/to must be ISO 8601 instants (e.g. 2026-09-12T00:00:00Z)"
            });

        var filter = new ExecutionQueryFilter(workflowId, normalizedStatus, fromUtc, toUtc, page, pageSize);
        return Ok(await _observability.GetExecutionsAsync(tenantId, filter, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Server-side aggregates for the caller's tenant: status counts, step latency by step type,
    /// and failure/retry summary — all within the optional workflow/time window.
    /// Step-level detail is sampled deterministically to bound memory: at most 50,000 of the most
    /// recent in-window step tuples (by execution start, descending) are aggregated, so under very
    /// heavy windows the latency/retry numbers describe the most recent activity rather than the
    /// full window.
    /// </summary>
    [HttpGet("aggregates")]
    [ProducesResponseType(typeof(ExecutionAggregates), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ExecutionAggregates>> GetAggregates(
        [FromQuery] Guid? workflowId,
        [FromQuery] string? from,
        [FromQuery] string? to)
    {
        if (!this.TryGetTenantId(out var tenantId))
            return Unauthorized();

        if (!TryParseWindow(from, to, out var fromUtc, out var toUtc))
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid time window",
                Status = StatusCodes.Status400BadRequest,
                Detail = "from/to must be ISO 8601 instants (e.g. 2026-09-12T00:00:00Z)"
            });

        var filter = new AggregateQueryFilter(workflowId, fromUtc, toUtc);
        return Ok(await _observability.GetAggregatesAsync(tenantId, filter, HttpContext.RequestAborted));
    }
}