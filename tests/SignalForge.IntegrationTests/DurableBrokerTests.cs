using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalForge.Application.Broker;
using SignalForge.Application.Data;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Broker;
using SignalForge.Infrastructure.Data;
using SignalForge.Worker;
using SignalForge.Worker.Services;

namespace SignalForge.IntegrationTests;

/// <summary>
/// C1: the durable broker transport. <c>Broker:Provider=Sql</c> appends published messages to the
/// shared SQL database (same store as the outbox), so messages published by a worker survive a
/// worker restart. The provider switch is exercised through the same <see cref="MessageBrokerRegistration"/>
/// the worker host uses.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class DurableBrokerTests : ApiTestBase
{
    private static readonly Guid TenantId = Guid.NewGuid();

    public DurableBrokerTests(MsSqlContainerFixture database)
        : base(database)
    {
    }

    private static ServiceProvider BuildWorkerServices(
        DbContextOptions<SignalForgeDbContext> options,
        string brokerProvider)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Broker:Provider"] = brokerProvider
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ISignalForgeDbContext>(_ => new SignalForgeDbContext(options));
        services.AddScoped<IOutboxMessageSender, OutboxMessageSender>();
        services.AddMessageBroker(config);
        return services.BuildServiceProvider();
    }

    private static OutboxProcessor BuildProcessor(
        ServiceProvider worker,
        OutboxOptions outboxOptions)
        => new OutboxProcessor(
            worker.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(outboxOptions),
            NullLogger<OutboxProcessor>.Instance);

    private static OutboxOptions FastOptions() => new()
    {
        BatchSize = 100,
        MaxAttempts = 3,
        PollIntervalSeconds = 1,
        FailureThreshold = 3,
        MaxBackoffSeconds = 300
    };

    [Fact]
    public async Task Sql_broker_publishes_via_real_outbox_flow_and_survives_restart()
    {
        var options = DbOptions();
        int baselineBrokerCount;
        using (var ctx = new SignalForgeDbContext(options))
            baselineBrokerCount = ctx.BrokerMessages.Count();

        try
        {
            using (var ctx = new SignalForgeDbContext(options))
            {
                ctx.Tenants.Add(Tenant.CreateWithId(TenantId, "itest-durable-broker"));
                await ctx.SaveChangesAsync();
                await new OutboxPublisher(ctx).PublishAsync(TenantId, "OrderCreated", "{\"orderId\":1}");
                await new OutboxPublisher(ctx).PublishAsync(TenantId, "PaymentReceived", "{\"paymentId\":2}");
            }

            // "Worker #1" publishes two outbox messages to the SQL-backed broker.
            using (var worker = BuildWorkerServices(options, "Sql"))
            {
                var processor = BuildProcessor(worker, FastOptions());
                await processor.ProcessBatchAsync(CancellationToken.None);

                using var ctx = new SignalForgeDbContext(options);
                var dbOutbox = await ctx.OutboxMessages
                    .Where(m => m.TenantId == TenantId)
                    .Select(m => new { m.IsProcessed, m.FailedAt, m.AttemptCount, m.ClaimedAt })
                    .ToListAsync();
                Assert.True(dbOutbox.Count > 0, $"no outbox rows? {dbOutbox.Count}");
                Assert.Equal(2, dbOutbox.Count);
                Assert.All(dbOutbox, m => Assert.True(m.IsProcessed, "message not processed"));

                using var scope = worker.CreateScope();
                Assert.IsType<SqlMessageBroker>(scope.ServiceProvider.GetRequiredService<IMessageBroker>());
                var firstAudit = await scope.ServiceProvider.GetRequiredService<IBrokerAudit>()
                    .GetAllAsync(CancellationToken.None);
                Assert.Equal(baselineBrokerCount + 2, firstAudit.Count);
            }

            // "Worker restart": an entirely fresh container resolves a brand-new broker instance
            // against the same SQL database, and the previously published messages are still there.
            using (var restarted = BuildWorkerServices(options, "Sql"))
            using (var scope = restarted.CreateScope())
            {
                var audit = scope.ServiceProvider.GetRequiredService<IBrokerAudit>();
                var survived = await audit.GetAllAsync(CancellationToken.None);

                Assert.Equal(baselineBrokerCount + 2, survived.Count);
                var ours = survived.Skip(baselineBrokerCount).ToArray();
                Assert.Equal("OrderCreated", ours[0].Type);
                Assert.Equal("PaymentReceived", ours[1].Type);
                Assert.Equal("{\"orderId\":1}", ours[0].Payload);
            }

            // The outbox rows were still acked (processed, not left dead-lettered).
            using (var verify = new SignalForgeDbContext(options))
            {
                Assert.Empty(await verify.DeadLetterMessages
                    .Where(d => d.TenantId == TenantId)
                    .ToListAsync());
                var persisted = await verify.OutboxMessages
                    .Where(m => m.TenantId == TenantId)
                    .ToListAsync();
                Assert.Equal(2, persisted.Count);
                Assert.All(persisted, m => Assert.True(m.IsProcessed));
            }
        }
        finally
        {
            await CleanupAsync(TenantId);
        }
    }

    [Theory]
    [InlineData("InMemory", typeof(InMemoryMessageBroker))]
    [InlineData("Sql", typeof(SqlMessageBroker))]
    public async Task Broker_Provider_Config_selects_the_transport_backend(
        string provider,
        Type expectedBrokerType)
    {
        var options = DbOptions();

        using (var worker = BuildWorkerServices(options, provider))
        using (var scope = worker.CreateScope())
        {
            Assert.Equal(expectedBrokerType,
                scope.ServiceProvider.GetRequiredService<IMessageBroker>().GetType());
            Assert.Equal(expectedBrokerType,
                scope.ServiceProvider.GetRequiredService<IBrokerAudit>().GetType());
        }

        await Task.CompletedTask;
    }
}