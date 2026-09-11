using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Application.Services;

/// <summary>
/// Filters for the paged execution list endpoint. All values are optional; the tenant is always
/// applied separately. Dates are UTC ISO 8601 instants compared against the execution start.
/// </summary>
public sealed record ExecutionQueryFilter(
    Guid? WorkflowId,
    string? Status,
    DateTime? FromUtc,
    DateTime? ToUtc,
    int Page,
    int PageSize);

/// <summary>One execution row for the observability list (joins workflow + version names).</summary>
public sealed record ExecutionSummary(
    Guid Id,
    Guid WorkflowId,
    string WorkflowName,
    int WorkflowVersionNumber,
    string Status,
    int CurrentStepNumber,
    DateTime StartedAt,
    DateTime? CompletedAt,
    int RetryCount,
    string? ErrorMessage);

/// <summary>Server-side paged result (page is 1-based).</summary>
public sealed record PagedExecutionsResult(
    IReadOnlyList<ExecutionSummary> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>Time-window filter for the aggregate endpoint (compared against execution StartedAt).</summary>
public sealed record AggregateQueryFilter(
    Guid? WorkflowId,
    DateTime? FromUtc,
    DateTime? ToUtc);

public sealed record StatusCount(string Status, int Count);

public sealed record CauseCount(string Cause, int Count);

/// <summary>Latency of completed step executions, grouped per step type (milliseconds).</summary>
public sealed record StepLatencyAggregate(
    string StepType,
    int Count,
    double AverageMs,
    int MinMs,
    int MaxMs);

/// <summary>Failure + retry summary for the window.</summary>
public sealed record FailureSummary(
    int FailedExecutionCount,
    IReadOnlyList<CauseCount> ExecutionFailureCauses,
    int RetriedExecutionCount,
    int TotalStepRetries,
    IReadOnlyList<CauseCount> StepFailureCauses);

/// <summary>Dashboard-ready aggregates, all computed server-side.</summary>
public sealed record ExecutionAggregates(
    Guid? WorkflowId,
    DateTime? FromUtc,
    DateTime? ToUtc,
    IReadOnlyList<StatusCount> StatusCounts,
    IReadOnlyList<StepLatencyAggregate> StepLatency,
    FailureSummary Failures);

/// <summary>
/// Read-only observability queries over workflow executions and step executions. Kept separate
/// from the orchestrator so mutation logic stays focused. Every query is scoped to
/// a single tenant on the denormalized <c>TenantId</c> execution column.
/// </summary>
public interface IExecutionObservabilityService
{
    /// <summary>Paged, filtered execution list for a tenant.</summary>
    Task<PagedExecutionsResult> GetExecutionsAsync(
        Guid tenantId,
        ExecutionQueryFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>Server-side status counts, step latency by step type, and failure/retry summary.</summary>
    Task<ExecutionAggregates> GetAggregatesAsync(
        Guid tenantId,
        AggregateQueryFilter filter,
        CancellationToken cancellationToken = default);
}