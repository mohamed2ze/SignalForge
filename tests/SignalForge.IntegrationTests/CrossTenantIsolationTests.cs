using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.IntegrationTests;

/// <summary>
/// Proves tenant isolation is enforced on every tenant-facing query path, using two real
/// tenants (A and B) with their own API keys against the production services and a live SQL
/// Server. Reserved prefix bytes keep the two keys distinct at first-8-chars lookup.
/// </summary>
[Collection(MsSqlCollection.Name)]
public class CrossTenantIsolationTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly string KeyA = $"aA{Guid.NewGuid():N}tenantA";
    private static readonly string KeyB = $"bB{Guid.NewGuid():N}tenantB";

    private readonly MsSqlContainerFixture _database;

    public CrossTenantIsolationTests(MsSqlContainerFixture database)
    {
        _database = database;
    }

    private DbContextOptions<SignalForgeDbContext> TestOptions()
        => new DbContextOptionsBuilder<SignalForgeDbContext>().UseSqlServer(_database.ConnectionString).Options;

    private static async Task SeedAsync(DbContextOptions<SignalForgeDbContext> options)
    {
        using var ctx = new SignalForgeDbContext(options);
        ctx.Tenants.Add(Tenant.CreateWithId(TenantA, "itest-tenant-a"));
        ctx.Tenants.Add(Tenant.CreateWithId(TenantB, "itest-tenant-b"));
        ctx.ApiKeys.Add(ApiKey.Create(TenantA, "itest-key-a", KeyA));
        ctx.ApiKeys.Add(ApiKey.Create(TenantB, "itest-key-b", KeyB));
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task ApiKeys_Resolve_Only_Their_Own_Tenant()
    {
        var options = TestOptions();

        try
        {
            await SeedAsync(options);

            using var ctx = new SignalForgeDbContext(options);
            var validator = new ApiKeyValidationService(ctx);

            var resultA = await validator.ValidateApiKeyAsync(KeyA);
            Assert.True(resultA.IsValid);
            Assert.Equal(TenantA, (Guid)resultA.TenantId!.Value);

            var resultB = await validator.ValidateApiKeyAsync(KeyB);
            Assert.True(resultB.IsValid);
            Assert.Equal(TenantB, (Guid)resultB.TenantId!.Value);
            Assert.NotEqual((Guid)resultA.TenantId!.Value, (Guid)resultB.TenantId!.Value);
        }
        finally
        {
            await CleanupAsync(options);
        }
    }

    [Fact]
    public async Task Workflows_Cannot_Be_Cross_Read()
    {
        var options = TestOptions();

        try
        {
            await SeedAsync(options);

            using var ctx = new SignalForgeDbContext(options);
            var serviceA = new WorkflowService(ctx, NullLogger<WorkflowService>.Instance);
            var serviceB = new WorkflowService(ctx, NullLogger<WorkflowService>.Instance);

            // Tenant A creates a workflow, a version (with a step), and publishes it.
            var workflow = await serviceA.CreateWorkflowAsync(TenantA, "secret-wf-a");
            var version = await serviceA.CreateVersionAsync(workflow.Id, TenantA, "v1");
            using (var draftCtx = new SignalForgeDbContext(options))
            {
                var draft = await draftCtx.WorkflowVersions
                    .SingleAsync(v => v.Id == version!.Id);
                draft.AddStep(1, "Delay", "{\"seconds\":1}", "initial");
                await draftCtx.SaveChangesAsync();
            }
            var published = await serviceA.PublishVersionAsync(workflow.Id, version!.Id, TenantA);
            Assert.NotNull(published);
            Assert.True(published!.IsPublished);
            Assert.NotEmpty(await serviceA.GetWorkflowsAsync(TenantA));

            // Tenant B sees none of it and cannot mutate it.
            Assert.Empty(await serviceB.GetWorkflowsAsync(TenantB));
            Assert.Null(await serviceB.GetWorkflowByIdAsync(workflow.Id, TenantB));
            Assert.Null(await serviceB.UpdateWorkflowAsync(workflow.Id, TenantB, "stolen"));
            Assert.False(await serviceB.DeleteWorkflowAsync(workflow.Id, TenantB));
            Assert.Null(await serviceB.CreateVersionAsync(workflow.Id, TenantB, "stolen-v"));
            Assert.Null(await serviceB.PublishVersionAsync(workflow.Id, version.Id, TenantB));

            // Tenant A's own workflow survives all the B attempts.
            var stillThere = await serviceA.GetWorkflowByIdAsync(workflow.Id, TenantA);
            Assert.NotNull(stillThere);
            Assert.Equal("secret-wf-a", stillThere!.Name);
        }
        finally
        {
            await CleanupAsync(options);
        }
    }

    [Fact]
    public async Task Events_Cannot_Be_Cross_Read()
    {
        var options = TestOptions();

        try
        {
            await SeedAsync(options);

            using var ctx = new SignalForgeDbContext(options);
            var outboxPublisher = new OutboxPublisher(ctx);
            var ingestA = new EventIngestionService(ctx, outboxPublisher);
            var ingestB = new EventIngestionService(ctx, outboxPublisher);

            var createdA = await ingestA.IngestEventAsync(
                TenantA, "ext-l4-1", "order.created", DateTime.UtcNow, "{\"orderId\":1}");
            Assert.True(createdA.IsNewEvent);

            // Tenant B cannot see A's event, even with its ID.
            Assert.Null(await ingestB.GetEventByIdAsync(createdA.Event.Id, TenantB));

            // The same external id is a NEW event in tenant B's namespace, and idempotent in A's.
            var createdB = await ingestB.IngestEventAsync(
                TenantB, "ext-l4-1", "order.created", DateTime.UtcNow, "{\"orderId\":2}");
            Assert.True(createdB.IsNewEvent);
            Assert.NotEqual(createdA.Event.Id, createdB.Event.Id);

            var duplicateA = await ingestA.IngestEventAsync(
                TenantA, "ext-l4-1", "order.created", DateTime.UtcNow, "{\"orderId\":1}");
            Assert.False(duplicateA.IsNewEvent);

            Assert.Equal(createdA.Event.Id, (await ingestA.GetEventByIdAsync(createdA.Event.Id, TenantA))!.Id);
        }
        finally
        {
            await CleanupAsync(options);
        }
    }

    [Fact]
    public async Task Executions_Cannot_Be_Cross_Read()
    {
        var options = TestOptions();

        try
        {
            await SeedAsync(options);

            Guid executionId;
            using (var ctx = new SignalForgeDbContext(options))
            {
                var workflow = Workflow.Create(TenantA, "wf-a");
                var version = WorkflowVersion.Create(workflow.Id, 1);
                var @event = Event.Create(TenantA, "ext-exec-1", "order.created", DateTime.UtcNow, "{}");
                var execution = WorkflowExecution.Create(workflow.Id, version.Id, @event.Id, TenantA);
                ctx.AddRange(workflow, version, @event, execution);
                await ctx.SaveChangesAsync();
                executionId = execution.Id;
            }

            using var readCtx = new SignalForgeDbContext(options);
            var emptyProvider = new ServiceCollection().BuildServiceProvider();
            var orchestratorA = new WorkflowExecutionOrchestratorService(
                readCtx, emptyProvider, NullLogger<WorkflowExecutionOrchestratorService>.Instance);

            Assert.NotNull(await orchestratorA.GetWorkflowExecutionByIdAsync(executionId, TenantA));
            Assert.Null(await orchestratorA.GetWorkflowExecutionByIdAsync(executionId, TenantB));
        }
        finally
        {
            await CleanupAsync(options);
        }
    }

    [Fact]
    public async Task DeadLetters_Cannot_Be_Cross_Read()
    {
        var options = TestOptions();

        try
        {
            await SeedAsync(options);

            Guid deadLetterId;
            using (var ctx = new SignalForgeDbContext(options))
            {
                var outbox = OutboxMessage.Create(TenantA, "Test/Fail", "{}");
                var deadLetter = DeadLetterMessage.CreateFromOutboxMessage(outbox, "boom");
                ctx.OutboxMessages.Add(outbox);
                ctx.DeadLetterMessages.Add(deadLetter);
                await ctx.SaveChangesAsync();
                deadLetterId = deadLetter.Id;
            }

            using var readCtx = new SignalForgeDbContext(options);
            var serviceA = new DeadLetterProcessingService(readCtx);
            var serviceB = new DeadLetterProcessingService(readCtx);

            Assert.NotEmpty(await serviceA.GetDeadLettersAsync(TenantA));

            Assert.Empty(await serviceB.GetDeadLettersAsync(TenantB));
            Assert.Null(await serviceB.GetDeadLetterByIdAsync(deadLetterId, TenantB));
            Assert.False(await serviceB.MarkAsProcessedAsync(deadLetterId, TenantB));

            var countsB = await serviceB.GetDeadLetterCountsAsync(TenantB);
            Assert.Equal(0, countsB.Total);

            Assert.NotNull(await serviceA.GetDeadLetterByIdAsync(deadLetterId, TenantA));
        }
        finally
        {
            await CleanupAsync(options);
        }
    }

    private static async Task CleanupAsync(DbContextOptions<SignalForgeDbContext> options)
    {
        using var cleanup = new SignalForgeDbContext(options);

        foreach (var tenantId in new[] { TenantA, TenantB })
        {
            // FK-safe order: children referencing NoAction parents go first (executions before
            // workflows/events/versions), dead letters before the executions they may reference.
            cleanup.DeadLetterMessages.RemoveRange(await cleanup.DeadLetterMessages.IgnoreQueryFilters()
                .Where(e => e.TenantId == tenantId).ToListAsync());
            cleanup.OutboxMessages.RemoveRange(await cleanup.OutboxMessages.IgnoreQueryFilters()
                .Where(e => e.TenantId == tenantId).ToListAsync());
            cleanup.WorkflowExecutions.RemoveRange(await cleanup.WorkflowExecutions.IgnoreQueryFilters()
                .Where(e => e.TenantId == tenantId).ToListAsync());
            cleanup.ApiKeys.RemoveRange(await cleanup.ApiKeys.IgnoreQueryFilters()
                .Where(k => k.TenantId == tenantId).ToListAsync());
            cleanup.Events.RemoveRange(await cleanup.Events.IgnoreQueryFilters()
                .Where(e => e.TenantId == tenantId).ToListAsync());
            cleanup.Workflows.RemoveRange(await cleanup.Workflows.IgnoreQueryFilters()
                .Where(w => w.TenantId == tenantId).ToListAsync());
            cleanup.Tenants.RemoveRange(await cleanup.Tenants.IgnoreQueryFilters()
                .Where(t => t.Id == tenantId).ToListAsync());
        }

        await cleanup.SaveChangesAsync();
    }
}