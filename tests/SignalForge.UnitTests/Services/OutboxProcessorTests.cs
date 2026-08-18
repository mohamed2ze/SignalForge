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

    // ---------- publish failure → retry with backoff ----------

    [Fact]
    public async Task Failed_cycle_keeps_message_repollable_and_grows_attempt()
    {
        var options = NewInMemoryOptions();
        await SeedMessageAsync(options, "OrderCreated", """{"id":1}""");

        var sender = new ControlledSender { Throw = true };
        var processor = CreateProcessor(BuildScopeFactory(options, sender));

        var firstDelay = await processor.ProcessBatchAsync(CancellationToken.None);

        // Under the circuit threshold: healthy pacing on the wire.
        Assert.Equal(TimeSpan.FromSeconds(5), firstDelay);

        using (var verify = new SignalForgeDbContext(options))
        {
            var message = await verify.OutboxMessages.SingleAsync();
            Assert.Equal(1, message.AttemptCount);
            Assert.False(message.IsProcessed);
            Assert.NotNull(message.FailedAt);
            Assert.Contains("Simulated send failure", message.ErrorMessage);
        }

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

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await processor.ProcessBatchAsync(CancellationToken.None);
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

    // ---------- circuit breaker: open on repeated failures, reset on success ----------

    [Fact]
    public async Task Circuit_opens_after_failure_threshold_and_success_resets_it()
    {
        var options = NewInMemoryOptions();
        await SeedMessageAsync(options, "OrderCreated", """{"id":1}""");

        var sender = new ControlledSender { Throw = true };
        var processor = CreateProcessor(
            BuildScopeFactory(options, sender),
            new OutboxOptions
            {
                PollIntervalSeconds = 1,
                MaxAttempts = 5,
                FailureThreshold = 3,
                MaxBackoffSeconds = 16
            });

        // Cycles 1 & 2: below threshold, breaker still closed (healthy pacing on the wire).
        Assert.Equal(TimeSpan.FromSeconds(1), await processor.ProcessBatchAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(1), await processor.ProcessBatchAsync(CancellationToken.None));

        // Cycle 3: threshold hit, circuit opens — the delay is the grown backoff (2^3 = 8s).
        Assert.Equal(TimeSpan.FromSeconds(8), await processor.ProcessBatchAsync(CancellationToken.None));

        // Cycle 4: still open, backoff doubles to 16s (capped at MaxBackoffSeconds).
        Assert.Equal(TimeSpan.FromSeconds(16), await processor.ProcessBatchAsync(CancellationToken.None));

        // Success resets the breaker: the (still retryable) message acks and pacing returns to base.
        sender.Throw = false;
        Assert.Equal(TimeSpan.FromSeconds(1), await processor.ProcessBatchAsync(CancellationToken.None));
        Assert.Single(sender.Sent);

        using (var verify = new SignalForgeDbContext(options))
        {
            var message = await verify.OutboxMessages.SingleAsync();
            Assert.True(message.IsProcessed);
            Assert.Equal(4, message.AttemptCount);
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
}