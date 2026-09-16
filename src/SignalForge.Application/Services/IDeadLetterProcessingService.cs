using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

public interface IDeadLetterProcessingService
{
    Task<List<DeadLetterMessage>> GetDeadLettersAsync(
        Guid tenantId,
        int skip = 0,
        int take = 100,
        bool onlyUnprocessed = false,
        CancellationToken cancellationToken = default);

    Task<DeadLetterMessage?> GetDeadLetterByIdAsync(
        Guid deadLetterId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task<bool> MarkAsProcessedAsync(
        Guid deadLetterId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task<DeadLetterCounts> GetDeadLetterCountsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets dead letter messages for a tenant with the filter/paging contract: optional
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
    /// Replays a dead letter: requeues a fresh outbox message with the same
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

public class DeadLetterCounts
{
    public int Total { get; set; }
    public int Unprocessed { get; set; }
    public int Processed { get; set; }
}

/// <summary>
/// Filter + paging contract for the dead-letter list. All values optional except the
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

    NotFound,

    /// <summary>A replay is already in flight (unprocessed requeue); no duplicate created.</summary>
    AlreadyInFlight
}

/// <summary>Replay outcome plus the (updated) dead letter when found.</summary>
public sealed record DeadLetterReplayResult(
    DeadLetterReplayStatus Status,
    DeadLetterMessage? DeadLetter);