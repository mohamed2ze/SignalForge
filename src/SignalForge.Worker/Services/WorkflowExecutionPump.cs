using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalForge.Application.Data;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;

namespace SignalForge.Worker.Services;

/// <summary>
/// The workflow engine's production driver: without this nothing advances executions created by
/// <c>POST /workflows/{id}/execute</c> outside the test harness
/// (<see cref="IWorkflowExecutionOrchestratorService.AdvanceWorkflowExecutionAsync"/> had no
/// production caller). SignalForge is run by the Worker because the Worker owns the outbox loop
/// and structured logging already; the pump is the natural seat for a future distributed
/// scheduler.
/// </summary>
public class WorkflowExecutionPump : IWorkflowExecutionPump
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExecutionPumpOptions _options;
    private readonly ILogger<WorkflowExecutionPump> _logger;

    public WorkflowExecutionPump(
        IServiceScopeFactory scopeFactory,
        IOptions<ExecutionPumpOptions> options,
        ILogger<WorkflowExecutionPump> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TimeSpan> ProcessCycleAsync(CancellationToken cancellationToken = default)
    {
        // Fresh scope per cycle (mirrors OutboxProcessor): a short-lived DbContext avoids a
        // long-lived context holding stale change tracking across a long-running host.
        using var scope = _scopeFactory.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IWorkflowExecutionOrchestratorService>();

        try
        {
            var now = DateTime.UtcNow;
            var leaseWatermark = now.AddSeconds(-_options.ClaimLeaseSeconds);

            // Select candidates whose next step is not freshly in flight: a step must not be
            // double-started by repeated polls. Pending/Running marks are the crash-window
            // (created but never finished) or the reclaim-after-crash case respectively, so they
            // block candidacy ONLY while they are fresh (StartedAt within the claim lease). Once
            // the claimed execution's lease expires, the stale next step is requeued below and the
            // execution is admitted again — a crashed worker can never leave an execution parked.
            // Failed/Retrying steps are always allowed through — the orchestrator re-enters them in
            // place (retries on the same record) or fails the execution once attempts are
            // exhausted. Leased executions (ClaimedAt newer than the lease watermark) are invisible
            // to this worker, so two workers can never claim the same execution.
            var candidateIds = await dbContext.WorkflowExecutions
                .Where(e => e.Status == WorkflowExecutionStatus.Running)
                .Where(e => e.ClaimedAt == null || e.ClaimedAt < leaseWatermark)
                .Where(e => !dbContext.WorkflowStepExecutions.Any(se =>
                    se.WorkflowExecutionId == e.Id &&
                    se.StepNumber == e.CurrentStepNumber + 1 &&
                    (se.Status == WorkflowStepExecutionStatus.Pending ||
                     se.Status == WorkflowStepExecutionStatus.Running) &&
                    se.StartedAt >= leaseWatermark))
                .OrderBy(e => e.StartedAt)
                .ThenBy(e => e.Id) // Deterministic tiebreak between same-instant executions
                .Select(e => e.Id)
                .Take(_options.BatchSize)
                .ToListAsync(cancellationToken);

            _logger.LogDebug("Polled {Count} runnable workflow executions", candidateIds.Count);

            if (candidateIds.Count == 0)
                return TimeSpan.FromSeconds(_options.PollIntervalSeconds);

            // Atomic claim: only this cycle's executions, and only while they are still
            // unclaimed. If another worker claimed some between the select and here, the claim
            // affects fewer rows and the re-query below materializes only this cycle's own rows —
            // so the two workers never advance the same execution.
            var claimedIds = await ClaimAsync(dbContext, candidateIds, now, leaseWatermark, cancellationToken);

            if (claimedIds.Count == 0)
                return TimeSpan.FromSeconds(_options.PollIntervalSeconds);

            var candidates = await dbContext.WorkflowExecutions
                .Include(e => e.StepExecutions)
                .Where(e => claimedIds.Contains(e.Id))
                .OrderBy(e => e.StartedAt)
                .ThenBy(e => e.Id)
                .ToListAsync(cancellationToken);

            // Crash recovery: a Running step whose worker died mid-step (lease expired) must be
            // requeued to Pending before the orchestrator re-enters it, or the advancer would see
            // a live Running step and (correctly) decline to advance. The claim above guarantees no
            // other worker can be making progress on these rows right now.
            var requeuedAny = false;
            foreach (var candidate in candidates)
            {
                var staleNextSteps = candidate.StepExecutions
                    .Where(se => se.StepNumber == candidate.CurrentStepNumber + 1 &&
                                 se.Status == WorkflowStepExecutionStatus.Running &&
                                 se.StartedAt < leaseWatermark)
                    .ToList();

                foreach (var staleStep in staleNextSteps)
                {
                    staleStep.RequeueForRestart();
                    requeuedAny = true;
                    _logger.LogInformation(
                        "Requeued stale running step {stepExecutionId} of execution {executionId} " +
                        "after worker crash", staleStep.Id, candidate.Id);
                }
            }

            if (requeuedAny)
                await dbContext.SaveChangesAsync(cancellationToken);

            foreach (var candidate in candidates)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var progressed = await orchestrator.AdvanceWorkflowExecutionAsync(
                        candidate.Id, cancellationToken);

                    // Success path: release the lease so the next cycle (or another worker) may
                    // pick the execution up again. Runs in a separate save so a late failure in
                    // the release cannot hide the fully-persisted advance.
                    await ReleaseClaimAsync(dbContext, candidate, cancellationToken);

                    _logger.LogDebug(
                        "Advanced workflow execution {executionId}: progressed {progressed}",
                        candidate.Id, progressed);
                }
                catch (Exception ex)
                {
                    // One bad execution must not stall the rest of the batch. Release the lease
                    // immediately (rather than waiting out the full window) so a transient error
                    // does not park the execution: the next cycle re-polls it, and poison
                    // executions are ultimately dead-lettered by the step-retry machinery.
                    _logger.LogError(ex,
                        "Error advancing workflow execution {executionId}", candidate.Id);

                    try
                    {
                        await ReleaseClaimAsync(dbContext, candidate, CancellationToken.None);
                    }
                    catch (Exception releaseEx)
                    {
                        _logger.LogError(releaseEx,
                            "Failed to release lease on workflow execution {executionId}",
                            candidate.Id);
                    }
                }
            }

            return TimeSpan.FromSeconds(_options.PollIntervalSeconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in workflow execution pump cycle");
            return TimeSpan.FromSeconds(_options.PollIntervalSeconds);
        }
    }

    /// <summary>
    /// Releases this cycle's lease on <paramref name="candidate"/> and persists it. The advance
    /// already saved its own changes; this save only clears <see cref="WorkflowExecution.ClaimedAt"/>.
    /// </summary>
    private static async Task ReleaseClaimAsync(
        ISignalForgeDbContext dbContext,
        WorkflowExecution candidate,
        CancellationToken cancellationToken)
    {
        candidate.ReleaseClaim();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Atomically claims <paramref name="candidateIds"/>, stamping each claimed row with this
    /// cycle's <paramref name="now"/> so ownership is attributable. Returns only the rows this
    /// cycle actually owns; any row a competing worker claimed in the meantime is excluded.
    /// </summary>
    /// <remarks>
    /// Relational providers use a single <c>ExecuteUpdateAsync</c> that flips only still-claimable
    /// rows in one statement (the race-proof path used in production). The EF Core in-memory
    /// provider does not support ExecuteUpdate/ExecuteDelete, so the claim falls back to tracked
    /// entities re-checking the same guard — deterministic for the single-writer unit tests and
    /// logically equivalent for the provider's semantics.
    /// </remarks>
    private static async Task<HashSet<Guid>> ClaimAsync(
        ISignalForgeDbContext dbContext,
        List<Guid> candidateIds,
        DateTime now,
        DateTime leaseWatermark,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsRelational())
        {
            var affected = await dbContext.WorkflowExecutions
                .Where(e => candidateIds.Contains(e.Id))
                .Where(e => e.Status == WorkflowExecutionStatus.Running)
                .Where(e => e.ClaimedAt == null || e.ClaimedAt < leaseWatermark)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(e => e.ClaimedAt, now),
                    cancellationToken);

            if (affected == 0)
                return new HashSet<Guid>();
        }
        else
        {
            var pending = await dbContext.WorkflowExecutions
                .Where(e => candidateIds.Contains(e.Id))
                .Where(e => e.Status == WorkflowExecutionStatus.Running)
                .Where(e => e.ClaimedAt == null || e.ClaimedAt < leaseWatermark)
                .ToListAsync(cancellationToken);

            foreach (var execution in pending)
                execution.Claim(now);

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // Materialize exactly this cycle's claim. The candidateIds guard also covers the
        // (practically unreachable) clock collision where a peer stamps the same instant.
        return (await dbContext.WorkflowExecutions
            .Where(e => e.ClaimedAt == now && candidateIds.Contains(e.Id))
            .Select(e => e.Id)
            .ToListAsync(cancellationToken))
            .ToHashSet();
    }
}