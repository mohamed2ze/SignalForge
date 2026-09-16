using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Data;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

public class DeadLetterProcessingService : IDeadLetterProcessingService
{
    private readonly ISignalForgeDbContext _dbContext;
    private readonly IUniqueViolationDetector _uniqueViolationDetector;

    public DeadLetterProcessingService(
        ISignalForgeDbContext dbContext,
        IUniqueViolationDetector uniqueViolationDetector)
    {
        _dbContext = dbContext;
        _uniqueViolationDetector = uniqueViolationDetector;
    }

    public async Task<List<DeadLetterMessage>> GetDeadLettersAsync(
        Guid tenantId,
        int skip = 0,
        int take = 100,
        bool onlyUnprocessed = false,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.DeadLetterMessages
            .Where(dlm => dlm.TenantId == tenantId);

        if (onlyUnprocessed)
        {
            query = query.Where(dlm => !dlm.IsProcessed);
        }

        // Include navigation properties for eager loading
        query = query.Include(dlm => dlm.WorkflowExecution)
                    .Include(dlm => dlm.WorkflowStepExecution);

        return await query
            .OrderByDescending(dlm => dlm.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<PagedDeadLettersResult> GetDeadLettersPagedAsync(
        Guid tenantId,
        DeadLetterQueryFilter filter,
        CancellationToken cancellationToken = default)
    {
        if (filter.Page < 1)
            throw new ArgumentOutOfRangeException(nameof(filter), "Page must be >= 1");
        if (filter.PageSize < 1 || filter.PageSize > 100)
            throw new ArgumentOutOfRangeException(nameof(filter), "PageSize must be between 1 and 100");

        var query = _dbContext.DeadLetterMessages.Where(dlm => dlm.TenantId == tenantId);

        if (filter.OnlyUnprocessed)
            query = query.Where(dlm => !dlm.IsProcessed);

        if (filter.WorkflowId != null)
        {
            // Workflow attribution is via the provenance nav: step-failure dead letters carry the
            // workflow execution, so their workflow is reachable; outbox-originated dead letters
            // (no execution) are naturally excluded from a workflow filter.
            var workflowId = filter.WorkflowId.Value;
            query = query.Where(dlm => dlm.WorkflowExecution != null &&
                                       dlm.WorkflowExecution.WorkflowId == workflowId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Cause))
            query = query.Where(dlm => dlm.ErrorMessage.Contains(filter.Cause.Trim()));

        if (filter.FromUtc != null)
            query = query.Where(dlm => dlm.CreatedAt >= filter.FromUtc);

        if (filter.ToUtc != null)
            query = query.Where(dlm => dlm.CreatedAt <= filter.ToUtc);

        var totalCount = await query.CountAsync(cancellationToken);

        // Deterministic ordering: CreatedAt desc, then Id desc (CreatedAt can collide at
        // millisecond precision).
        var items = await query
            .OrderByDescending(dlm => dlm.CreatedAt)
            .ThenByDescending(dlm => dlm.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Include(dlm => dlm.WorkflowExecution)
            .Include(dlm => dlm.WorkflowStepExecution)
            .ToListAsync(cancellationToken);

        return new PagedDeadLettersResult(items, filter.Page, filter.PageSize, totalCount);
    }

    public async Task<DeadLetterReplayResult> ReplayAsync(
        Guid tenantId,
        Guid deadLetterId,
        CancellationToken cancellationToken = default)
    {
        var deadLetter = await _dbContext.DeadLetterMessages
            .FirstOrDefaultAsync(dlm =>
                dlm.Id == deadLetterId &&
                dlm.TenantId == tenantId, cancellationToken);

        if (deadLetter == null)
            return new DeadLetterReplayResult(DeadLetterReplayStatus.NotFound, null);

        // Idempotency rule: at most one in-flight requeue per dead letter. The
        // requeue is 'in flight' while an unprocessed outbox message tagged with this dead letter
        // exists; it clears as soon as that message is processed or exhausted (dead-lettered).
        var inFlight = await _dbContext.OutboxMessages.AnyAsync(
            m => m.ReplaySourceDeadLetterId == deadLetterId && !m.IsProcessed, cancellationToken);

        if (inFlight)
            return new DeadLetterReplayResult(DeadLetterReplayStatus.AlreadyInFlight, deadLetter);

        var outboxMessage = OutboxMessage.CreateReplay(
            tenantId,
            deadLetter.OriginalMessageType,
            deadLetter.OriginalPayload,
            deadLetter.Id);

        _dbContext.OutboxMessages.Add(outboxMessage);
        var previousReplayCount = deadLetter.ReplayCount;
        var previousLastReplayedAt = deadLetter.LastReplayedAt;
        deadLetter.RecordReplay();

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (_uniqueViolationDetector.IsUniqueViolation(ex))
        {
            // Two concurrent replays of the same dead letter both passed the in-flight check
            // above; the filtered unique index let only one requeue through. The
            // whole batch was rolled back at the DB, so unwind our in-memory changes and report
            // the idempotent outcome. (Remove on an Added entity detaches it; it will not be
            // re-inserted by a later SaveChanges in the same scope.)
            _dbContext.OutboxMessages.Remove(outboxMessage);
            deadLetter.RollBackReplay(previousReplayCount, previousLastReplayedAt);
            return new DeadLetterReplayResult(DeadLetterReplayStatus.AlreadyInFlight, deadLetter);
        }

        return new DeadLetterReplayResult(DeadLetterReplayStatus.Replayed, deadLetter);
    }

    public async Task<DeadLetterMessage?> GetDeadLetterByIdAsync(
        Guid deadLetterId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.DeadLetterMessages
            .Include(dlm => dlm.WorkflowExecution)
            .Include(dlm => dlm.WorkflowStepExecution)
            .FirstOrDefaultAsync(dlm =>
                dlm.Id == deadLetterId &&
                dlm.TenantId == tenantId, cancellationToken);
    }

    public async Task<bool> MarkAsProcessedAsync(
        Guid deadLetterId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var deadLetter = await _dbContext.DeadLetterMessages
            .FirstOrDefaultAsync(dlm =>
                dlm.Id == deadLetterId &&
                dlm.TenantId == tenantId, cancellationToken);

        if (deadLetter == null)
        {
            return false; // Dead letter not found or doesn't belong to tenant
        }

        deadLetter.MarkAsProcessed();
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<DeadLetterCounts> GetDeadLetterCountsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        var total = await _dbContext.DeadLetterMessages
            .CountAsync(dlm => dlm.TenantId == tenantId, cancellationToken);

        var unprocessed = await _dbContext.DeadLetterMessages
            .CountAsync(dlm => dlm.TenantId == tenantId && !dlm.IsProcessed, cancellationToken);

        var processed = total - unprocessed;

        return new DeadLetterCounts
        {
            Total = total,
            Unprocessed = unprocessed,
            Processed = processed
        };
    }
}