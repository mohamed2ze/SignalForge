using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.IntegrationTests;

[Collection(MsSqlCollection.Name)]
public class WorkflowVersioningTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private readonly MsSqlContainerFixture _database;

    public WorkflowVersioningTests(MsSqlContainerFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task CreateVersionAsync_PersistsIncrementedCopy_AsInsertedRow()
    {
        var options = new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseSqlServer(_database.ConnectionString)
            .Options;

        try
        {
            Guid workflowId;
            using (var ctx = new SignalForgeDbContext(options))
            {
                ctx.Tenants.Add(Tenant.CreateWithId(TenantId, "itest"));
                var workflow = Workflow.Create(TenantId, "itest-workflow");
                var v1 = workflow.CreateDraftVersion(1);
                v1.AddStep(1, "Delay", "{\"seconds\":1}", "initial", "first step");
                ctx.Workflows.Add(workflow);
                await ctx.SaveChangesAsync();
                workflowId = workflow.Id;
            }

            var service = new WorkflowService(
                new SignalForgeDbContext(options),
                NullLogger<WorkflowService>.Instance);

            // Regression guard: adding a draft via the tracked navigation used to be seen as
            // Modified (EF treats Guid keys as value-generated), producing an UPDATE against a
            // nonexistent row instead of an INSERT. Requires ValueGeneratedNever on Guid keys.
            var v2 = await service.CreateVersionAsync(workflowId, TenantId, "second draft");
            Assert.NotNull(v2);
            Assert.Equal(2, v2?.VersionNumber);
            Assert.False(v2?.IsPublished);

            using var verify = new SignalForgeDbContext(options);
            var persisted = await verify.WorkflowVersions
                .Where(wv => wv.WorkflowId == workflowId)
                .OrderBy(wv => wv.VersionNumber)
                .ToListAsync();
            Assert.Equal(2, persisted.Count);
            Assert.Equal(2, persisted[1].VersionNumber);

            // Copy semantics: the new draft should have carried over the step from v1.
            var persistedV2 = await verify.WorkflowVersions
                .Include(wv => wv.Steps)
                .SingleAsync(wv => wv.WorkflowId == workflowId && wv.VersionNumber == 2);
            Assert.Single(persistedV2.Steps);
            Assert.Equal("Delay", persistedV2.Steps.Single().StepType);
        }
        finally
        {
            using var cleanup = new SignalForgeDbContext(options);
            var workflows = await cleanup.Workflows.IgnoreQueryFilters()
                .Where(w => w.TenantId == TenantId)
                .ToListAsync();
            cleanup.Workflows.RemoveRange(workflows);
            var tenant = await cleanup.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == TenantId);
            if (tenant != null)
                cleanup.Tenants.Remove(tenant);
            await cleanup.SaveChangesAsync();
        }
    }
}