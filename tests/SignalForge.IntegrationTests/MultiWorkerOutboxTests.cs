using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalForge.Application.Data;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Broker;
using SignalForge.Infrastructure.Data;
using SignalForge.Worker;
using SignalForge.Worker.Services;

namespace SignalForge.IntegrationTests;

/// <summary>
/// H2: multiple worker processes polling the same SQL-backed outbox + broker store must not
/// deliver the same message twice. The atomic claim (ExecuteUpdateAsync on ClaimedAt) is the only
/// guarantee; each seeded message must be acked exactly once and published to the durable broker
/// exactly once, with no dead letters.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class MultiWorkerOutboxTests : ApiTestBase
{
    private static readonly Guid TenantId = Guid.NewGuid();

    public MultiWorkerOutboxTests(MsSqlContainerFixture database)
        : base(database)
    {
    }

    private static OutboxProcessor BuildWorker(
        DbContextOptions<SignalForgeDbContext> options)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Broker:Provider"] = "Sql"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ISignalForgeDbContext>(_ => new SignalForgeDbContext(options));
        services.AddScoped<IOutboxMessageSender, OutboxMessageSender>();
        services.AddMessageBroker(config);

        return new OutboxProcessor(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OutboxOptions
            {
                BatchSize = 100,
                MaxAttempts = 3,
                PollIntervalSeconds = 1,
                FailureThreshold = 3,
                MaxBackoffSeconds = 300
            }),
            NullLogger<OutboxProcessor>.Instance);
    }

    [Fact]
    public async Task Four_workers_never_deliver_the_same_outbox_message_twice()
    {
        var options = DbOptions();
        const int messageCount = 20;
        var seededTypes = Enumerable.Range(0, messageCount).Select(i => $"W{i}").ToHashSet();

        int baselineBrokerCount;
        using (var ctx = new SignalForgeDbContext(options))
            baselineBrokerCount = ctx.BrokerMessages.Count();

        try
        {
            using (var ctx = new SignalForgeDbContext(options))
            {
                ctx.Tenants.Add(Tenant.CreateWithId(TenantId, "itest-multi-worker"));
                for (var i = 0; i < messageCount; i++)
                {
                    await new OutboxPublisher(ctx).PublishAsync(
                        TenantId, $"W{i}", $"{{\"index\":{i}}}");
                }
                await ctx.SaveChangesAsync();
            }

            var workers = Enumerable.Range(0, 4)
                .Select(_ => BuildWorker(options))
                .ToArray();

            // Hammer the same store with four independent workers, exactly once per cycle, so the
            // atomic claim is routinely contended across overlapping cycles.
            for (var cycle = 0; cycle < 8; cycle++)
            {
                await Task.WhenAll(workers.Select(w => w.ProcessBatchAsync(CancellationToken.None)));
            }

            using var verify = new SignalForgeDbContext(options);

            var outboxRows = await verify.OutboxMessages
                .Where(m => m.TenantId == TenantId)
                .ToListAsync();

            var deadLetters = await verify.DeadLetterMessages
                .Where(d => d.TenantId == TenantId)
                .ToListAsync();
            Assert.Empty(deadLetters);

            // Every message acked exactly once; nothing left claimed in-flight.
            Assert.Equal(messageCount, outboxRows.Count);
            Assert.All(outboxRows, m =>
            {
                Assert.True(m.IsProcessed);
                Assert.NotNull(m.ProcessedAt);
                Assert.Null(m.ClaimedAt);
                Assert.Equal(0, m.AttemptCount);
            });

            // The durable broker received each seeded type exactly once across all workers -
            // no duplicate deliveries despite concurrent claiming.
            var brokerRows = await verify.BrokerMessages
                .OrderBy(m => m.PublishedAt)
                .ToListAsync();
            var ours = brokerRows.Skip(baselineBrokerCount).ToList();
            Assert.Equal(messageCount, ours.Count);
            Assert.Equal(seededTypes, ours.Select(m => m.Type).ToHashSet(), StringComparer.Ordinal);
            foreach (var g in ours.GroupBy(m => m.Type))
                Assert.Single(g.ToList());
        }
        finally
        {
            await CleanupAsync(TenantId);
        }
    }
}