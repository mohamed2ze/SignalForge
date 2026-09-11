using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalForge.Application.Broker;
using SignalForge.Application.Data;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Broker;
using SignalForge.Infrastructure.Data;
using SignalForge.Worker.Services;

namespace SignalForge.IntegrationTests;

[Collection(MsSqlCollection.Name)]
public class OutboxBrokerEndToEndTests : ApiTestBase
{
    private static readonly Guid TenantId = Guid.NewGuid();

    public OutboxBrokerEndToEndTests(MsSqlContainerFixture database)
        : base(database)
    {
    }

    private static OutboxProcessor CreateProcessor(
        DbContextOptions<SignalForgeDbContext> options,
        IOutboxMessageSender sender,
        OutboxOptions outboxOptions)
    {
        var services = new ServiceCollection();
        services.AddScoped<ISignalForgeDbContext>(_ => new SignalForgeDbContext(options));
        services.AddScoped<IOutboxMessageSender>(_ => sender);

        var provider = services.BuildServiceProvider();
        return new OutboxProcessor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(outboxOptions),
            NullLogger<OutboxProcessor>.Instance);
    }

    [Fact]
    public async Task OutboxMessage_Transits_Into_InMemoryBroker()
    {
        var options = DbOptions();

        try
        {
            using (var ctx = new SignalForgeDbContext(options))
            {
                ctx.Tenants.Add(Tenant.CreateWithId(TenantId, "itest-broker"));
                await ctx.SaveChangesAsync();
            }

            // A client-side publish writes to the outbox table (this is the API/sender path).
            using (var ctx = new SignalForgeDbContext(options))
            {
                await new OutboxPublisher(ctx).PublishAsync(TenantId, "OrderCreated", "{\"orderId\":1}");
            }

            var broker = new InMemoryMessageBroker();
            var sender = new OutboxMessageSender(broker, NullLogger<OutboxMessageSender>.Instance);
            var processor = CreateProcessor(options, sender, new OutboxOptions
            {
                BatchSize = 100,
                MaxAttempts = 3,
                PollIntervalSeconds = 1,
                FailureThreshold = 3,
                MaxBackoffSeconds = 300
            });

            await processor.ProcessBatchAsync(CancellationToken.None);

            // The outbox message visibly transited into the in-memory broker store.
            var published = broker.GetAll();
            Assert.Single(published);
            Assert.Equal("OrderCreated", published[0].Type);
            Assert.Equal("{\"orderId\":1}", published[0].Payload);

            // And it was acked in the outbox table (processed, not dead-lettered).
            using (var verify = new SignalForgeDbContext(options))
            {
                var persisted = await verify.OutboxMessages
                    .Where(m => m.TenantId == TenantId)
                    .ToListAsync();
                Assert.Single(persisted);
                Assert.All(persisted, m =>
                {
                    Assert.True(m.IsProcessed);
                    Assert.NotNull(m.ProcessedAt);
                });
            }
        }
        finally
        {
            await CleanupAsync(TenantId);
        }
    }

    [Fact]
    public async Task TestFail_Still_DeadLetters_After_Broker_Swap()
    {
        var options = DbOptions();

        try
        {
            using (var ctx = new SignalForgeDbContext(options))
            {
                ctx.Tenants.Add(Tenant.CreateWithId(TenantId, "itest-broker"));
                await ctx.SaveChangesAsync();
                await new OutboxPublisher(ctx).PublishAsync(TenantId, "Test/Fail", "{\"boom\":true}");
            }

            var broker = new InMemoryMessageBroker();
            var sender = new OutboxMessageSender(broker, NullLogger<OutboxMessageSender>.Instance);
            var processor = CreateProcessor(options, sender, new OutboxOptions
            {
                BatchSize = 100,
                MaxAttempts = 3,
                PollIntervalSeconds = 1,
                FailureThreshold = 3,
                MaxBackoffSeconds = 300
            });

            // MaxAttempts = 3 failing cycles => attempt 1, 2, then dead-letter on the 3rd. The
            // per-message retry gate is collapsed between cycles so the attempts
            // run back-to-back here instead of over the real 2s/4s backoff.
            for (var i = 0; i < 3; i++)
            {
                await processor.ProcessBatchAsync(CancellationToken.None);
                if (i < 2)
                    await MakeRetryDueAsync(options);
            }

            // Nothing reached the broker (the Test/Fail seam throws before publishing).
            Assert.Empty(broker.GetAll());

            using (var verify = new SignalForgeDbContext(options))
            {
                var deadLetters = await verify.DeadLetterMessages
                    .Where(d => d.TenantId == TenantId)
                    .ToListAsync();
                var remaining = await verify.OutboxMessages
                    .Where(m => m.TenantId == TenantId)
                    .ToListAsync();

                Assert.Single(deadLetters);
                Assert.Equal("Test/Fail", deadLetters[0].OriginalMessageType);
                Assert.Equal(3, deadLetters[0].FinalAttemptCount);
                Assert.Empty(remaining);
            }
        }
        finally
        {
            await CleanupAsync(TenantId);
        }
    }

    // Collapses the per-message retry gate for the tenant's in-flight message so the next processor
    // cycle re-claims it. Uses the EF value-sink idiom from the observability tests.
    private static async Task MakeRetryDueAsync(DbContextOptions<SignalForgeDbContext> options)
    {
        using var ctx = new SignalForgeDbContext(options);
        var message = await ctx.OutboxMessages.Where(m => m.TenantId == TenantId).SingleAsync();
        ctx.Entry(message).Property(m => m.NextRetryAt).CurrentValue = DateTime.UtcNow.AddSeconds(-1);
        await ctx.SaveChangesAsync();
    }
}