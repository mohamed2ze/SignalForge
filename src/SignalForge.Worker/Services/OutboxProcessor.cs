using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalForge.Application.Data;
using SignalForge.Application.Security;
using SignalForge.Domain.Models;

namespace SignalForge.Worker.Services;

/// <summary>
/// Implementation of the outbox processor that reads and sends messages from the outbox table.
/// Registered as a singleton: it owns the poll loop's pacing and circuit-breaker state, and
/// creates a fresh DI scope per poll cycle so scoped services (DbContext, sender) never leak
/// across cycles.
/// </summary>
public class OutboxProcessor : IOutboxProcessor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxProcessor> _logger;

    // Pacing / circuit-breaker state (persists for the worker's lifetime).
    private int _consecutiveFailures;
    private TimeSpan _currentBackoff;
    private bool _circuitOpen;

    public OutboxProcessor(
        IServiceScopeFactory scopeFactory,
        IOptions<OutboxOptions> options,
        ILogger<OutboxProcessor> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _currentBackoff = TimeSpan.FromSeconds(_options.PollIntervalSeconds);
    }

    public async Task<TimeSpan> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Fresh scope per cycle: a short-lived DbContext avoids a long-lived
            // context holding stale change tracking and connections across a long-running host.
            // Scope/DI failure is a whole-cycle failure like any other and is caught below.
            using var scope = _scopeFactory.CreateScope();

            var dbContext = scope.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();
            var sender = scope.ServiceProvider.GetRequiredService<IOutboxMessageSender>();

            // Claim unprocessed messages. Failed-but-not-exhausted messages (FailedAt set,
            // AttemptCount less than MaxAttempts) are retried, but only once their per-message
            // retry gate has passed: a poison message is scheduled (NextRetryAt)
            // and not re-claimed on consecutive cycles, so a single failing message can no longer
            // tight-loop the broker. Only dead-lettered messages disappear.
            // Claims are leased (ClaimedAt): a row claimed by a still-live worker is invisible to
            // every other worker, so a crashed worker's claim expires only after
            // ClaimLeaseSeconds — preventing duplicate sends while a slow worker is mid-batch.
            var now = DateTime.UtcNow;
            var leaseWatermark = now.AddSeconds(-_options.ClaimLeaseSeconds);

            var scanLimit = _options.BatchSize * _options.ScanMultiplier;
            var scanned = await dbContext.OutboxMessages
                .Where(m => !m.IsProcessed)
                .Where(m => m.FailedAt == null || m.NextRetryAt == null || m.NextRetryAt <= now)
                .Where(m => m.ClaimedAt == null || m.ClaimedAt < leaseWatermark)
                .Where(m => dbContext.Tenants.Any(t => t.Id == m.TenantId && t.DeletedAt == null))
                .OrderBy(m => m.CreatedAt)
                .ThenBy(m => m.Id) // Deterministic tiebreak between same-instant messages
                .Select(m => new { m.Id, m.TenantId })
                .Take(scanLimit)
                .ToListAsync(cancellationToken);

            // Deactivated (soft-deleted) tenants are filtered above so their messages are neither
            // published nor retried. Per-tenant fairness: a flood from one tenant fills only its
            // round-robin share of the batch, never the whole batch.
            var candidateIds = FairBatchSelector.PickFairBatch(
                scanned.Select(s => (s.Id, s.TenantId)).ToList(),
                _options.BatchSize);

            _logger.LogDebug("Polled {Count} candidate outbox messages", candidateIds.Count);

            if (candidateIds.Count == 0)
            {
                ResetBreaker();
                return TimeSpan.FromSeconds(_options.PollIntervalSeconds);
            }

            // Atomic claim: transition exactly the rows this cycle selected — as long as they are
            // still unclaimed. If another worker claimed some between the select and here, the
            // claim affects fewer rows and the re-query below materializes only this cycle's own
            // rows, so the two workers never deliver the same message.
            var claimedIds = await ClaimAsync(dbContext, candidateIds, now, leaseWatermark, cancellationToken);

            if (claimedIds.Count == 0)
            {
                ResetBreaker();
                return TimeSpan.FromSeconds(_options.PollIntervalSeconds);
            }

            var messages = await dbContext.OutboxMessages
                .Where(m => claimedIds.Contains(m.Id))
                .OrderBy(m => m.CreatedAt)
                .ThenBy(m => m.Id)
                .ToListAsync(cancellationToken);

            _logger.LogInformation("Processing {Count} outbox messages", messages.Count);

            foreach (var message in messages)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    await sender.SendAsync(message.Type, message.Payload, cancellationToken);

                    // Ack after successful send. Note: if the process crashes between the send
                    // and this save, the message is re-polled (once its lease lapses) and may be
                    // sent twice. Exactly-once delivery requires broker-side de-duplication,
                    // deferred to a later stage; the outbox pattern guarantees at-least-once, with
                    // the claim minimizing the crash window to the send progress, not the batch.
                    message.MarkAsProcessed();
                    message.ReleaseClaim();
                    _logger.LogDebug("Processed outbox message {messageId} for tenant {tenantId}",
                        message.Id, message.TenantId);
                }
                catch (Exception ex)
                {
                    var errorText = StorageText.ScrubForStorage(ex.ToString())!;
                    message.IncrementAttempt();
                    message.MarkAsFailed(errorText);

                    if (message.AttemptCount >= _options.MaxAttempts)
                    {
                        // Exhausted retries: move to the dead-letter table and stop retrying.
                        var deadLetter = DeadLetterMessage.CreateFromOutboxMessage(message, errorText);
                        dbContext.DeadLetterMessages.Add(deadLetter);
                        dbContext.OutboxMessages.Remove(message);

                        _logger.LogWarning(ex,
                            "Outbox message {messageId} for tenant {tenantId} exceeded max attempts " +
                            "({MaxAttempts}), moved to dead letter {deadLetterId}",
                            message.Id, message.TenantId, _options.MaxAttempts, deadLetter.Id);
                    }
                    else
                    {
                        // Per-message exponential backoff: the message is not
                        // re-claimed until the backoff elapses. Per-message failures do not grow
                        // the global cycle backoff or open the circuit -- the loop is functioning
                        // normally; only the individual message is unhealthy.
                        var retryBackoffSeconds = Math.Min(
                            Math.Pow(2, message.AttemptCount),
                            _options.MaxBackoffSeconds);
                        message.ScheduleNextRetry(DateTime.UtcNow.AddSeconds(retryBackoffSeconds));
                        message.ReleaseClaim();

                        _logger.LogWarning(ex,
                            "Failed to process outbox message {messageId} for tenant {tenantId} " +
                            "(attempt {attempt}/{MaxAttempts}), retrying in {BackoffSeconds}s",
                            message.Id, message.TenantId, message.AttemptCount, _options.MaxAttempts,
                            retryBackoffSeconds);
                    }
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            // Per-message failures never reach here as cycle failures: they are
            // individually scheduled via NextRetryAt and reported in the per-message log above.
            // The global circuit only opens for whole-cycle exceptions (handled in the outer catch).
            ResetBreaker();

            // Once the circuit opens, the grown backoff paces the whole loop so repeated
            // failures stop tight-looping the broker/DB.
            return _circuitOpen
                ? _currentBackoff
                : TimeSpan.FromSeconds(_options.PollIntervalSeconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure();
            _logger.LogError(ex, "Error in outbox poll cycle");
            return _currentBackoff;
        }
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
            var affected = await dbContext.OutboxMessages
                .Where(m => candidateIds.Contains(m.Id))
                .Where(m => m.ClaimedAt == null || m.ClaimedAt < leaseWatermark)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(m => m.ClaimedAt, now),
                    cancellationToken);

            if (affected == 0)
                return new HashSet<Guid>();
        }
        else
        {
            var pending = await dbContext.OutboxMessages
                .Where(m => candidateIds.Contains(m.Id))
                .Where(m => m.ClaimedAt == null || m.ClaimedAt < leaseWatermark)
                .ToListAsync(cancellationToken);

            foreach (var message in pending)
                message.Claim(now);

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // Materialize exactly this cycle's claim. The candidateIds guard also covers the
        // (practically unreachable) clock collision where a peer stamps the same instant.
        return (await dbContext.OutboxMessages
            .Where(m => m.ClaimedAt == now && candidateIds.Contains(m.Id))
            .Select(m => m.Id)
            .ToListAsync(cancellationToken))
            .ToHashSet();
    }

    /// <summary>
    /// Records a failing cycle and grows the backoff exponentially, doubling each time until
    /// the configured maximum. After <see cref="OutboxOptions.FailureThreshold"/> consecutive
    /// failures the circuit is considered open and the poll pauses for the grown backoff.
    /// </summary>
    private void RecordFailure()
    {
        _consecutiveFailures++;

        var nextBackoff = TimeSpan.FromSeconds(_currentBackoff.TotalSeconds * 2);
        _currentBackoff = nextBackoff > TimeSpan.FromSeconds(_options.MaxBackoffSeconds)
            ? TimeSpan.FromSeconds(_options.MaxBackoffSeconds)
            : nextBackoff;

        if (_consecutiveFailures >= _options.FailureThreshold)
        {
            _circuitOpen = true;
            _logger.LogWarning(
                "Outbox circuit open: {ConsecutiveFailures} consecutive failing cycles, " +
                "pausing {BackoffSeconds}s before next attempt",
                _consecutiveFailures, _currentBackoff.TotalSeconds);
        }
    }

    private void ResetBreaker()
    {
        _consecutiveFailures = 0;
        _currentBackoff = TimeSpan.FromSeconds(_options.PollIntervalSeconds);
        _circuitOpen = false;
    }
}