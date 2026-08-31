using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

/// <summary>
/// Service for processing dead letter messages.
/// </summary>
public interface IDeadLetterProcessingService
{
    /// <summary>
    /// Gets dead letter messages for a tenant with optional filtering.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="skip">Number of records to skip for pagination</param>
    /// <param name="take">Number of records to take</param>
    /// <param name="onlyUnprocessed">If true, returns only unprocessed dead letters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A list of dead letter messages</returns>
    Task<List<DeadLetterMessage>> GetDeadLettersAsync(
        Guid tenantId,
        int skip = 0,
        int take = 100,
        bool onlyUnprocessed = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a dead letter message by its ID for a specific tenant.
    /// </summary>
    /// <param name="deadLetterId">The dead letter message ID</param>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The dead letter message if found and belongs to the tenant, otherwise null</returns>
    Task<DeadLetterMessage?> GetDeadLetterByIdAsync(
        Guid deadLetterId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a dead letter message as processed (manually handled).
    /// </summary>
    /// <param name="deadLetterId">The dead letter message ID</param>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the dead letter was found and marked as processed, false otherwise</returns>
    Task<bool> MarkAsProcessedAsync(
        Guid deadLetterId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets counts of dead letter messages for a tenant.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dead letter counts</returns>
    Task<DeadLetterCounts> GetDeadLetterCountsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets dead letter messages for a tenant with the Level 5 filter/paging contract: optional
    /// workflow id (joins via the step-execution provenance), cause substring on the final error
    /// message, processed-state flag, and a CreatedAt time window. Always resolved with an exact
    /// total count, 1-based paging, and a deterministic order (CreatedAt desc, Id desc).
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="filter">The paging + filter parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A paged tile of dead letters + total count</returns>
    Task<PagedDeadLettersResult> GetDeadLettersPagedAsync(
        Guid tenantId,
        DeadLetterQueryFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replays a dead letter (Decision #25): requeues a fresh outbox message with the same
    /// tenant/type/payload and records the replay on the dead letter. At most one in-flight requeue
    /// per dead letter — a second replay while the requeue is still unprocessed returns
    /// <see cref="DeadLetterReplayStatus.AlreadyInFlight"/> without creating a duplicate. The dead
    /// letter stays visible; if the requeue fails terminally again it lands as a NEW dead letter
    /// whose <c>ReplayedFromDeadLetterId</c> points back here.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="deadLetterId">The dead letter to replay</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The replay outcome, with the dead letter when found</returns>
    Task<DeadLetterReplayResult> ReplayAsync(
        Guid tenantId,
        Guid deadLetterId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Counts of dead letter messages.
/// </summary>
public class DeadLetterCounts
{
    public int Total { get; set; }
    public int Unprocessed { get; set; }
    public int Processed { get; set; }
}

/// <summary>
/// Filter + paging contract for the Level 5 dead-letter list. All values optional except the
/// tenant, which is applied separately; windows compare against <c>CreatedAt</c>.
/// </summary>
public sealed record DeadLetterQueryFilter(
    Guid? WorkflowId,
    string? Cause,
    bool OnlyUnprocessed,
    DateTime? FromUtc,
    DateTime? ToUtc,
    int Page,
    int PageSize);

/// <summary>Server-side paged dead-letter result (page is 1-based).</summary>
public sealed record PagedDeadLettersResult(
    IReadOnlyList<DeadLetterMessage> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>Outcome of a dead-letter replay request.</summary>
public enum DeadLetterReplayStatus
{
    /// <summary>A requeue was created and the replay recorded on the dead letter.</summary>
    Replayed,

    /// <summary>The dead letter does not exist for this tenant.</summary>
    NotFound,

    /// <summary>A replay is already in flight (unprocessed requeue); no duplicate created.</summary>
    AlreadyInFlight
}

/// <summary>Replay outcome plus the (updated) dead letter when found.</summary>
public sealed record DeadLetterReplayResult(
    DeadLetterReplayStatus Status,
    DeadLetterMessage? DeadLetter);