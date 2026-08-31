using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Data;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

/// <summary>
/// Tenant-scoped execution observability queries (Decision #24). Windows are applied server-side
/// against the execution <see cref="WorkflowExecution.StartedAt"/>; step latency/failure details
/// are projected to scalar tuples in SQL and aggregated post-projection, which keeps the queries
/// portable across EF providers while still returning fully aggregated dashboard contracts.
/// </summary>
public class ExecutionObservabilityService : IExecutionObservabilityService
{
    private const int MaxFailureCauses = 10;

    private readonly ISignalForgeDbContext _dbContext;

    public ExecutionObservabilityService(ISignalForgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<PagedExecutionsResult> GetExecutionsAsync(
        Guid tenantId,
        ExecutionQueryFilter filter,
        CancellationToken cancellationToken = default)
    {
        if (filter.Page < 1)
            throw new ArgumentOutOfRangeException(nameof(filter), "Page must be >= 1");
        if (filter.PageSize < 1 || filter.PageSize > 100)
            throw new ArgumentOutOfRangeException(nameof(filter), "PageSize must be between 1 and 100");

        var query = BuildScopedQuery(tenantId, filter.WorkflowId, filter.FromUtc, filter.ToUtc);

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = filter.Status.Trim();
            query = query.Where(e => e.Status == status);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(e => e.StartedAt)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(e => new ExecutionSummary(
                e.Id,
                e.WorkflowId,
                e.Workflow.Name,
                e.WorkflowVersion.VersionNumber,
                e.Status,
                e.CurrentStepNumber,
                e.StartedAt,
                e.CompletedAt,
                e.RetryCount,
                e.ErrorMessage))
            .ToListAsync(cancellationToken);

        return new PagedExecutionsResult(items, filter.Page, filter.PageSize, totalCount);
    }

    /// <inheritdoc />
    public async Task<ExecutionAggregates> GetAggregatesAsync(
        Guid tenantId,
        AggregateQueryFilter filter,
        CancellationToken cancellationToken = default)
    {
        var executions = BuildScopedQuery(tenantId, filter.WorkflowId, filter.FromUtc, filter.ToUtc);

        // Status counts (grouped server-side).
        var statusCounts = await executions
            .GroupBy(e => e.Status)
            .Select(g => new StatusCount(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        var failedExecutions = await executions
            .Where(e => e.Status == WorkflowExecutionStatus.Failed)
            .CountAsync(cancellationToken);

        var executionFailureCauses = await executions
            .Where(e => e.Status == WorkflowExecutionStatus.Failed && e.ErrorMessage != null)
            .GroupBy(e => e.ErrorMessage!)
            .OrderByDescending(g => g.Count())
            .Take(MaxFailureCauses)
            .Select(g => new CauseCount(g.Key, g.Count()))
            .ToListAsync(cancellationToken);

        var retriedExecutionCount = await executions
            .Where(e => e.RetryCount > 0)
            .CountAsync(cancellationToken);

        // Step latency: completed steps of in-window executions, aggregated post-projection so the
        // query stays provider-portable (TimeSpan Min/Average/Max are not reliably translatable).
        var stepTuples = await (from e in executions
                                from se in e.StepExecutions
                                where se.Status == WorkflowStepExecutionStatus.Succeeded &&
                                      se.CompletedAt != null
                                select new { se.WorkflowStep.StepType, se.StartedAt, se.CompletedAt })
            .ToListAsync(cancellationToken);

        var latency = stepTuples
            .GroupBy(x => x.StepType)
            .Select(g =>
            {
                var durations = g
                    .Select(x => (x.CompletedAt!.Value - x.StartedAt).TotalMilliseconds)
                    .ToList();
                return new StepLatencyAggregate(
                    g.Key,
                    durations.Count,
                    durations.Average(),
                    (int)durations.Min(),
                    (int)durations.Max());
            })
            .OrderBy(a => a.StepType)
            .ToList();

        // Step retries + failure causes, likewise projected then grouped.
        var stepExecutions = await (from e in executions
                                    from se in e.StepExecutions
                                    select new { se.AttemptNumber, se.ErrorMessage, se.Status })
            .ToListAsync(cancellationToken);

        var totalStepRetries = stepExecutions.Count(se => se.AttemptNumber > 1);

        var stepFailureCauses = stepExecutions
            .Where(se => se.Status == WorkflowStepExecutionStatus.Failed &&
                         se.ErrorMessage != null)
            .GroupBy(se => se.ErrorMessage!)
            .OrderByDescending(g => g.Count())
            .Take(MaxFailureCauses)
            .Select(g => new CauseCount(g.Key, g.Count()))
            .ToList();

        var failures = new FailureSummary(
            failedExecutions,
            executionFailureCauses,
            retriedExecutionCount,
            totalStepRetries,
            stepFailureCauses);

        return new ExecutionAggregates(
            filter.WorkflowId,
            filter.FromUtc,
            filter.ToUtc,
            statusCounts,
            latency,
            failures);
    }

    private IQueryable<WorkflowExecution> BuildScopedQuery(
        Guid tenantId,
        Guid? workflowId,
        DateTime? fromUtc,
        DateTime? toUtc)
    {
        var query = _dbContext.WorkflowExecutions.Where(e => e.TenantId == tenantId);

        if (workflowId != null)
            query = query.Where(e => e.WorkflowId == workflowId);

        if (fromUtc != null)
            query = query.Where(e => e.StartedAt >= fromUtc);

        if (toUtc != null)
            query = query.Where(e => e.StartedAt <= toUtc);

        return query;
    }
}