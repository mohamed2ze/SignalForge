using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalForge.Application.Data;
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

    /// <inheritdoc />
    public async Task<TimeSpan> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        // Fresh scope per cycle: a short-lived DbContext avoids a long-lived
        // context holding stale change tracking and connections across a long-running host.
        using var scope = _scopeFactory.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IOutboxMessageSender>();

        try
        {
            // Claim unprocessed messages. Failed-but-not-exhausted messages (FailedAt set,
            // AttemptCount less than MaxAttempts) are retried; only dead-lettered messages disappear.
            var messages = await dbContext.OutboxMessages
                .Where(m => !m.IsProcessed)
                .OrderBy(m => m.CreatedAt)
                .Take(_options.BatchSize)
                .ToListAsync(cancellationToken);

            _logger.LogDebug("Polled {Count} outbox messages", messages.Count);

            if (messages.Count == 0)
            {
                ResetBreaker();
                return TimeSpan.FromSeconds(_options.PollIntervalSeconds);
            }

            _logger.LogInformation("Processing {Count} outbox messages", messages.Count);

            var cycleFailures = 0;
            foreach (var message in messages)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    await sender.SendAsync(message.Type, message.Payload, cancellationToken);

                    // Ack after successful send. Note: if the process crashes between the send
                    // and this save, the message is re-polled and may be sent twice. Exactly-once
                    // delivery requires broker-side de-duplication, deferred to a later stage;
                    // the outbox pattern guarantees at-least-once.
                    message.MarkAsProcessed();
                    _logger.LogDebug("Processed outbox message {messageId} for tenant {tenantId}",
                        message.Id, message.TenantId);
                }
                catch (Exception ex)
                {
                    message.IncrementAttempt();
                    message.MarkAsFailed(ex.ToString());
                    cycleFailures++;

                    if (message.AttemptCount >= _options.MaxAttempts)
                    {
                        // Exhausted retries: move to the dead-letter table and stop retrying.
                        var deadLetter = DeadLetterMessage.CreateFromOutboxMessage(message, ex.ToString());
                        dbContext.DeadLetterMessages.Add(deadLetter);
                        dbContext.OutboxMessages.Remove(message);

                        _logger.LogWarning(ex,
                            "Outbox message {messageId} for tenant {tenantId} exceeded max attempts " +
                            "({MaxAttempts}), moved to dead letter {deadLetterId}",
                            message.Id, message.TenantId, _options.MaxAttempts, deadLetter.Id);
                    }
                    else
                    {
                        _logger.LogWarning(ex,
                            "Failed to process outbox message {messageId} for tenant {tenantId} " +
                            "(attempt {attempt}/{MaxAttempts})",
                            message.Id, message.TenantId, message.AttemptCount, _options.MaxAttempts);
                    }
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            if (cycleFailures > 0)
                RecordFailure();
            else
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