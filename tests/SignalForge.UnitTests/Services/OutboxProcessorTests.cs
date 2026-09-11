using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalForge.Application.Data;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Broker;
using SignalForge.Infrastructure.Data;
using SignalForge.Worker.Services;

namespace SignalForge.UnitTests.Services;

/// <summary>
/// Deterministic unit tests for <see cref="OutboxProcessor"/>. The processor never sleeps:
/// it returns the next poll delay as a <see cref="TimeSpan"/>, so pacing, backoff and the
/// circuit breaker are all observable through the return value without any Task.Delay in tests.
/// </summary>
public class OutboxProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    // A fake sender whose failure behavior can be toggled per test/cycle.
    private sealed class ControlledSender : IOutboxMessageSender
    {
        public bool Throw { get; set; } = true;
        public List<(string Type, string Payload)> Sent { get; } = new();

        public Task SendAsync(string type, string payload, CancellationToken cancellationToken = default)
        {
            if (Throw)
                throw new InvalidOperationException($"Simulated send failure for {type}");

            Sent.Add((type, payload));
            return Task.CompletedTask;
        }
    }

    // Wraps a real scope factory but can fail at scope creation — the whole-cycle exception path
    // (vs. the per-message failure path exercised by the ControlledSender).
    private sealed class FlakyScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceScopeFactory _inner;
        public bool Fail { get; set; }

        public FlakyScopeFactory(IServiceScopeFactory inner) => _inner = inner;

        public IServiceScope CreateScope()
            => Fail ? throw new InvalidOperationException("Simulated scope creation failure") : _inner.CreateScope();
    }

    private static DbContextOptions<SignalForgeDbContext> NewInMemoryOptions()
        => new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static IServiceScopeFactory BuildScopeFactory(
        DbContextOptions<SignalForgeDbContext> options,
        IOutboxMessageSender sender)
    {
        var services = new ServiceCollection();
        services.AddScoped<ISignalForgeDbContext>(_ => new SignalForgeDbContext(options));
        services.AddScoped<IOutboxMessageSender>(_ => sender);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static OutboxProcessor CreateProcessor(
        IServiceScopeFactory scopeFactory,
        OutboxOptions? options = null)
    {
        options ??= new OutboxOptions();
        return new OutboxProcessor(
            scopeFactory,
            Options.Create(options),
            NullLogger<OutboxProcessor>.Instance);
    }

    // ---------- happy path: poll → publish → ack ----------

    [Fact]
    public async Task Poll_publishes_and_acknowledges_then_never_resends()
    {
        var options = NewInMemoryOptions();
        await SeedMessageAsync(options, "OrderCreated", """{"id":1}""");
        await SeedMessageAsync(options, "OrderCreated", """{"id":2}""");

        var broker = new InMemoryMessageBroker();
        var sender = new OutboxMessageSender(broker, NullLogger<OutboxMessageSender>.Instance);
        var processor = CreateProcessor(BuildScopeFactory(options, sender));

        var delay = await processor.ProcessBatchAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(5), delay);

        // Acked: IsProcessed set, ProcessedAt recorded, broker received both.
        using (var verify = new SignalForgeDbContext(options))
        {
            var persisted = await verify.OutboxMessages
                .Where(m => m.TenantId == TenantId)
                .ToListAsync();
            Assert.Equal(2, persisted.Count);
            Assert.All(persisted, m =>
            {
                Assert.True(m.IsProcessed);
                Assert.NotNull(m.ProcessedAt);
            });
        }

        Assert.Equal(2, broker.GetAll().Count);

        // Next cycle polls nothing new: still healthy pacing, nothing re-sent.
        var nextDelay = await processor.ProcessBatchAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(5), nextDelay);
        Assert.Equal(2, broker.GetAll().Count);
    }

    // ---------- publish failure → retry gate (NextRetryAt) then retry ----------

    [Fact]
    public async Task Failed_cycle_keeps_message_repollable_and_grows_attempt()
    {
        var options = NewInMemoryOptions();
        await SeedMessageAsync(options, "OrderCreated", """{"id":1}""");

        var sender = new ControlledSender { Throw = true };
        var processor = CreateProcessor(BuildScopeFactory(options, sender));

        var firstDelay = await processor.ProcessBatchAsync(CancellationToken.None);

        // Per-message failures never grow the global backoff: healthy pacing on
        // the wire.
        Assert.Equal(TimeSpan.FromSeconds(5), firstDelay);

        using (var verify = new SignalForgeDbContext(options))
        {
            var message = await verify.OutboxMessages.SingleAsync();
            Assert.Equal(1, message.AttemptCount);
            Assert.False(message.IsProcessed);
            Assert.NotNull(message.FailedAt);
            Assert.True(message.NextRetryAt > DateTime.UtcNow); // retry gate scheduled
            Assert.Contains("Simulated send failure", message.ErrorMessage);
        }

        // The retry gate (2s for attempt 1) has not elapsed: an immediate cycle claims nothing.
        await processor.ProcessBatchAsync(CancellationToken.None);

        using (var verify = new SignalForgeDbContext(options))
        {
            var message = await verify.OutboxMessages.SingleAsync();
            Assert.Equal(1, message.AttemptCount); // untouched
        }

        // Once the gate passes, the message is re-polled and the attempt grows.
        await MakeRetryDueAsync(options);
        await processor.ProcessBatchAsync(CancellationToken.None);

        using (var verify = new SignalForgeDbContext(options))
        {
            var message = await verify.OutboxMessages.SingleAsync();
            Assert.Equal(2, message.AttemptCount);
            Assert.False(message.IsProcessed);
        }
    }

    // ---------- persistent failure → dead-letter after MaxAttempts ----------

    [Fact]
    public async Task Persistent_failure_deadletters_after_max_attempts_and_is_never_repolled()
    {
        var options = NewInMemoryOptions();
        await SeedMessageAsync(options, "OrderCreated", """{"id":1}""");

        var sender = new ControlledSender { Throw = true };
        var processor = CreateProcessor(
            BuildScopeFactory(options, sender),
            new OutboxOptions { MaxAttempts = 3 });

        // Cycle, wait out the retry gate, cycle again — three attempts total.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await processor.ProcessBatchAsync(CancellationToken.None);
            if (attempt < 3)
                await MakeRetryDueAsync(options);
        }

        using (var verify = new SignalForgeDbContext(options))
        {
            Assert.Empty(await verify.OutboxMessages.Where(m => m.TenantId == TenantId).ToListAsync());

            var deadLetter = await verify.DeadLetterMessages.SingleAsync();
            Assert.Equal("OrderCreated", deadLetter.OriginalMessageType);
            Assert.Equal(3, deadLetter.FinalAttemptCount);
        }

        // A further cycle finds nothing to pick up.
        var delay = await processor.ProcessBatchAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(5), delay);
        using (var verify = new SignalForgeDbContext(options))
        {
            Assert.Empty(await verify.OutboxMessages.Where(m => m.TenantId == TenantId).ToListAsync());
        }
    }

    // ---------- retry gate: a poison message is not re-claimed until NextRetryAt ----------

    [Fact]
    public async Task NextRetryAt_gates_repolling()
    {
        var options = NewInMemoryOptions();
        await SeedMessageAsync(options, "OrderCreated", """{"id":1}""");

        var sender = new ControlledSender { Throw = true };
        var processor = CreateProcessor(
            BuildScopeFactory(options, sender),
            new OutboxOptions { MaxAttempts = 5 });

        await processor.ProcessBatchAsync(CancellationToken.None);
        await processor.ProcessBatchAsync(CancellationToken.None);

        // Two consecutive cycles, no gate elapsed: only one attempt logged, message untouched.
        using (var verify = new SignalForgeDbContext(options))
        {
            var message = await verify.OutboxMessages.SingleAsync();
            Assert.Equal(1, message.AttemptCount);
        }

        await MakeRetryDueAsync(options);
        await processor.ProcessBatchAsync(CancellationToken.None);

        using (var verify = new SignalForgeDbContext(options))
        {
            var message = await verify.OutboxMessages.SingleAsync();
            Assert.Equal(2, message.AttemptCount);
            Assert.False(message.IsProcessed);
        }
    }

    // ---------- per-message failures never grow the global backoff or open the circuit ----------

    [Fact]
    public async Task Message_level_failures_do_not_grow_global_backoff_or_open_circuit()
    {
        var options = NewInMemoryOptions();
        await SeedMessageAsync(options, "OrderCreated", """{"id":1}""");

        var sender = new ControlledSender { Throw = true };
        var processor = CreateProcessor(
            BuildScopeFactory(options, sender),
            new OutboxOptions
            {
                PollIntervalSeconds = 1,
                MaxAttempts = 10,
                FailureThreshold = 3,
                MaxBackoffSeconds = 16
            });

        // Three consecutive failing cycles (past the old threshold) with a gate wait between them.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(TimeSpan.FromSeconds(1), await processor.ProcessBatchAsync(CancellationToken.None));
            await MakeRetryDueAsync(options);
        }

        // The message is still healthy-pacing: the loop is functioning, only the message is bad.
        using (var verify = new SignalForgeDbContext(options))
        {
            var message = await verify.OutboxMessages.SingleAsync();
            Assert.Equal(3, message.AttemptCount);
            Assert.False(message.IsProcessed);
        }
    }

    // ---------- Test/Fail seam: throws before the broker, still dead-letters ----------

    [Fact]
    public async Task TestFail_throws_before_broker_and_still_deadletters()
    {
        var options = NewInMemoryOptions();
        await SeedMessageAsync(options, "Test/Fail", """{"boom":true}""");

        var broker = new InMemoryMessageBroker();
        var sender = new OutboxMessageSender(broker, NullLogger<OutboxMessageSender>.Instance);
        var processor = CreateProcessor(
            BuildScopeFactory(options, sender),
            new OutboxOptions { MaxAttempts = 1 });

        await processor.ProcessBatchAsync(CancellationToken.None);

        // Nothing ever reached the broker (the seam throws before PublishAsync).
        Assert.Empty(broker.GetAll());

        using (var verify = new SignalForgeDbContext(options))
        {
            Assert.Empty(await verify.OutboxMessages.Where(m => m.TenantId == TenantId).ToListAsync());
            var deadLetter = await verify.DeadLetterMessages.SingleAsync();
            Assert.Equal("Test/Fail", deadLetter.OriginalMessageType);
            Assert.Equal(1, deadLetter.FinalAttemptCount);
        }
    }

    // ---------- circuit breaker: opens only on whole-cycle failures, resets on success ----------

    [Fact]
    public async Task Circuit_opens_after_whole_cycle_failure_threshold_and_success_resets_it()
    {
        var options = NewInMemoryOptions();
        await SeedMessageAsync(options, "OrderCreated", """{"id":1}""");

        var sender = new ControlledSender { Throw = false };
        var scopeFactory = new FlakyScopeFactory(BuildScopeFactory(options, sender));
        var processor = CreateProcessor(
            scopeFactory,
            new OutboxOptions
            {
                PollIntervalSeconds = 1,
                MaxAttempts = 5,
                FailureThreshold = 3,
                MaxBackoffSeconds = 16
            });

        // Cycles 1 & 2: below threshold, breaker still closed (growing backoff, still pre-circuit).
        scopeFactory.Fail = true;
        Assert.Equal(TimeSpan.FromSeconds(2), await processor.ProcessBatchAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(4), await processor.ProcessBatchAsync(CancellationToken.None));

        // Cycle 3: threshold hit, circuit opens — the delay is now the grown backoff.
        Assert.Equal(TimeSpan.FromSeconds(8), await processor.ProcessBatchAsync(CancellationToken.None));

        // Cycle 4: still open, backoff doubles to 16s (capped at MaxBackoffSeconds).
        Assert.Equal(TimeSpan.FromSeconds(16), await processor.ProcessBatchAsync(CancellationToken.None));

        // Success resets the breaker: the scope resolves, the message acks, pacing returns to base.
        scopeFactory.Fail = false;
        Assert.Equal(TimeSpan.FromSeconds(1), await processor.ProcessBatchAsync(CancellationToken.None));
        Assert.Single(sender.Sent);

        using (var verify = new SignalForgeDbContext(options))
        {
            var message = await verify.OutboxMessages.SingleAsync();
            Assert.True(message.IsProcessed);
            Assert.Equal(0, message.AttemptCount); // fresh send, never retried
        }
    }

    private static async Task SeedMessageAsync(
        DbContextOptions<SignalForgeDbContext> options,
        string type,
        string payload)
    {
        await using var ctx = new SignalForgeDbContext(options);
        ctx.OutboxMessages.Add(OutboxMessage.Create(TenantId, type, payload));
        await ctx.SaveChangesAsync();
    }

    // Forces a scheduled retry to be due now: the processor only re-claims a failed message once
    // its NextRetryAt gate has passed, so tests that exercise consecutive attempts must collapse
    // the gate. Uses the EF value-sink the observability/replay tests already rely on.
    private static async Task MakeRetryDueAsync(DbContextOptions<SignalForgeDbContext> options)
    {
        await using var ctx = new SignalForgeDbContext(options);
        var message = await ctx.OutboxMessages.SingleAsync();
        ctx.Entry(message).Property(m => m.NextRetryAt).CurrentValue = DateTime.UtcNow.AddSeconds(-1);
        await ctx.SaveChangesAsync();
    }
}