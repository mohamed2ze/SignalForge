using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.UnitTests.Services;

/// <summary>
/// Step-authoring service tests (C2): add/update/reorder/enable/disable on draft versions,
/// including the published-version immutability rule and per-type config validation.
/// </summary>
public class WorkflowServiceStepAuthoringTests
{
    private static SignalForgeDbContext CreateDb()
        => new(new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static WorkflowService CreateService(SignalForgeDbContext db)
        => new(db, NullLogger<WorkflowService>.Instance);

    private static async Task<(Guid TenantId, Guid WorkflowId, Guid VersionId)> SeedAsync(
        SignalForgeDbContext db, Action<WorkflowVersion> configure, bool publish = false)
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(Tenant.CreateWithId(tenantId, "authoring-test"));

        var workflow = Workflow.Create(tenantId, "authoring-wf");
        var version = workflow.CreateDraftVersion(1);
        configure(version);
        if (publish)
        {
            workflow.Enable();
            version.Publish();
        }

        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        return (tenantId, workflow.Id, version.Id);
    }

    [Fact]
    public async Task AddStep_appends_when_no_position_Given()
    {
        var db = CreateDb();
        var (tenantId, _, versionId) = await SeedAsync(db, v =>
            v.AddStep(1, "LogAudit", """{"message":"one"}"""));
        var service = CreateService(db);

        var step = await service.AddStepAsync(versionId, tenantId, new AddStepCommand(
            null, "Delay", """{"seconds":1}""", "gap"));

        Assert.NotNull(step);
        Assert.Equal(2, step!.StepNumber);
        Assert.Equal(2, (await db.WorkflowSteps.Where(s => s.WorkflowVersionId == versionId).ToListAsync()).Count);
    }

    [Fact]
    public async Task AddStep_at_explicit_position_shifts_following_steps()
    {
        var db = CreateDb();
        var (tenantId, _, versionId) = await SeedAsync(db, v =>
        {
            v.AddStep(1, "LogAudit", """{"message":"one"}""");
            v.AddStep(2, "LogAudit", """{"message":"two"}""");
        });
        var service = CreateService(db);

        var step = await service.AddStepAsync(versionId, tenantId, new AddStepCommand(
            1, "Delay", """{"seconds":0}""", "inserted"));

        Assert.NotNull(step);
        Assert.Equal(1, step!.StepNumber);
        var numbers = (await db.WorkflowSteps
                .Where(s => s.WorkflowVersionId == versionId)
                .ToListAsync())
            .OrderBy(s => s.StepNumber)
            .Select(s => s.StepNumber)
            .ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, numbers);
    }

    [Theory]
    [InlineData("Delay", """{"notSeconds":5}""")]
    [InlineData("MysteryType", """{"anything":1}""")]
    public async Task AddStep_rejects_invalid_configuration(string stepType, string config)
    {
        var db = CreateDb();
        var (tenantId, _, versionId) = await SeedAsync(db, _ => { });
        var service = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddStepAsync(versionId, tenantId, new AddStepCommand(null, stepType, config)));
    }

    [Fact]
    public async Task UpdateStep_rewrites_config_and_validates_it()
    {
        var db = CreateDb();
        var (tenantId, _, versionId) = await SeedAsync(db, v =>
            v.AddStep(1, "Delay", """{"seconds":5}""", "wait"));
        var service = CreateService(db);
        var stepId = (await db.WorkflowSteps.SingleAsync()).Id;

        var updated = await service.UpdateStepAsync(versionId, tenantId, stepId,
            new UpdateStepCommand(Configuration: """{"seconds":10}""", IsEnabled: false));

        Assert.NotNull(updated);
        Assert.False(updated!.IsEnabled);
        Assert.Contains("\"seconds\":10", updated.Configuration);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateStepAsync(versionId, tenantId, stepId,
                new UpdateStepCommand(Configuration: """{"bad":"x"}""")));
    }

    [Fact]
    public async Task Enable_disable_toggle_and_remove_renumbers()
    {
        var db = CreateDb();
        var (tenantId, _, versionId) = await SeedAsync(db, v =>
        {
            v.AddStep(1, "LogAudit", """{"message":"one"}""");
            v.AddStep(2, "LogAudit", """{"message":"two"}""");
            v.AddStep(3, "LogAudit", """{"message":"three"}""");
        });
        var service = CreateService(db);
        var steps = await db.WorkflowSteps.Where(s => s.WorkflowVersionId == versionId).ToListAsync();

        var disabled = await service.SetStepEnabledAsync(versionId, tenantId, steps[1].Id, enabled: false);
        Assert.NotNull(disabled);
        Assert.False(disabled!.IsEnabled);

        var reEnabled = await service.SetStepEnabledAsync(versionId, tenantId, steps[1].Id, enabled: true);
        Assert.True(reEnabled!.IsEnabled);

        var removed = await service.RemoveStepAsync(versionId, tenantId, steps[0].Id);
        Assert.True(removed);

        var remaining = (await db.WorkflowSteps
                .Where(s => s.WorkflowVersionId == versionId)
                .ToListAsync())
            .OrderBy(s => s.StepNumber)
            .ToList();
        Assert.Equal(2, remaining.Count);
        Assert.Equal(new[] { 1, 2 }, remaining.Select(s => s.StepNumber).ToArray());
        Assert.Equal(new[] { steps[1].Id, steps[2].Id }, remaining.Select(s => s.Id).ToArray());
    }

    [Fact]
    public async Task Reorder_applies_new_sequence()
    {
        var db = CreateDb();
        var (tenantId, _, versionId) = await SeedAsync(db, v =>
        {
            v.AddStep(1, "LogAudit", """{"message":"one"}""");
            v.AddStep(2, "LogAudit", """{"message":"two"}""");
        });
        var service = CreateService(db);
        var steps = await db.WorkflowSteps.Where(s => s.WorkflowVersionId == versionId).ToListAsync();

        var ok = await service.ReorderStepsAsync(
            versionId, tenantId, new[] { steps[1].Id, steps[0].Id });

        Assert.True(ok);
        var reordered = (await db.WorkflowSteps
                .Where(s => s.WorkflowVersionId == versionId)
                .ToListAsync())
            .OrderBy(s => s.StepNumber)
            .ToList();
        Assert.Equal(new[] { steps[1].Id, steps[0].Id }, reordered.Select(s => s.Id).ToArray());
    }

    [Fact]
    public async Task Reorder_rejects_mismatched_id_set()
    {
        var db = CreateDb();
        var (tenantId, _, versionId) = await SeedAsync(db, v =>
            v.AddStep(1, "LogAudit", """{"message":"one"}"""));
        var service = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReorderStepsAsync(versionId, tenantId, new[] { Guid.NewGuid() }));
    }

    [Fact]
    public async Task Published_version_is_immutable_across_all_mutations()
    {
        var db = CreateDb();
        var (tenantId, _, versionId) = await SeedAsync(db, v =>
            v.AddStep(1, "LogAudit", """{"message":"one"}"""), publish: true);
        var service = CreateService(db);
        var step = await db.WorkflowSteps.SingleAsync();

        async Task AssertRejected(Func<Task> operation)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(operation);
            Assert.Contains("published", ex.Message);
        }

        await AssertRejected(() => service.AddStepAsync(versionId, tenantId,
            new AddStepCommand(null, "Delay", """{"seconds":1}""")));
        await AssertRejected(() => service.UpdateStepAsync(versionId, tenantId, step.Id,
            new UpdateStepCommand(IsEnabled: false)));
        await AssertRejected(() => service.SetStepEnabledAsync(versionId, tenantId, step.Id, false));
        await AssertRejected(() => service.RemoveStepAsync(versionId, tenantId, step.Id));
        await AssertRejected(() => service.ReorderStepsAsync(versionId, tenantId, new[] { step.Id }));
    }

    [Fact]
    public async Task Mutations_are_tenant_scoped()
    {
        var db = CreateDb();
        var (_, _, versionId) = await SeedAsync(db, v =>
            v.AddStep(1, "LogAudit", """{"message":"one"}"""));
        var service = CreateService(db);
        var foreignTenant = Guid.NewGuid();

        var step = await service.AddStepAsync(versionId, foreignTenant, new AddStepCommand(
            null, "Delay", """{"seconds":1}"""));

        Assert.Null(step);
        Assert.False(await service.RemoveStepAsync(versionId, foreignTenant, Guid.NewGuid()));
    }
}