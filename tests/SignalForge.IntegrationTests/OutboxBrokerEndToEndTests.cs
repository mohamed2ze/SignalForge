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
public class OutboxBrokerEndToEndTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private readonly MsSqlContainerFixture _database;

    public OutboxBrokerEndToEndTests(MsSqlContainerFixture database)
    {
        _database = database;
    }

    private DbContextOptions<SignalForgeDbContext> TestOptions()
        => new DbContextOptionsBuilder<SignalForgeDbContext>().UseSqlServer(_database.ConnectionString).Options;

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
        var options = TestOptions();

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
            await CleanupAsync(options);
        }
    }

    [Fact]
    public async Task TestFail_Still_DeadLetters_After_Broker_Swap()
    {
        var options = TestOptions();

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

            // MaxAttempts = 3 failing cycles => attempt 1, 2, then dead-letter on the 3rd.
            await processor.ProcessBatchAsync(CancellationToken.None);
            await processor.ProcessBatchAsync(CancellationToken.None);
            await processor.ProcessBatchAsync(CancellationToken.None);

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
            await CleanupAsync(options);
        }
    }

    private static async Task CleanupAsync(DbContextOptions<SignalForgeDbContext> options)
    {
        using var cleanup = new SignalForgeDbContext(options);

        var deadLetters = await cleanup.DeadLetterMessages.IgnoreQueryFilters()
            .Where(d => d.TenantId == TenantId)
            .ToListAsync();
        cleanup.DeadLetterMessages.RemoveRange(deadLetters);

        var outbox = await cleanup.OutboxMessages.IgnoreQueryFilters()
            .Where(m => m.TenantId == TenantId)
            .ToListAsync();
        cleanup.OutboxMessages.RemoveRange(outbox);

        var tenant = await cleanup.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == TenantId);
        if (tenant != null)
            cleanup.Tenants.Remove(tenant);

        await cleanup.SaveChangesAsync();
    }
}