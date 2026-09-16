using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.IntegrationTests;

/// <summary>
/// Shared scaffolding for the integration suite: database access,
/// idempotent tenant seeding, FK-safe cleanup, workflow/execution seeding, and the common HTTP
/// workflow-surface helpers. Concrete classes keep their own <c>[Collection(MsSqlCollection.Name)]</c>
/// and per-test tenant identity; the base owns no collection so class-level sequencing is unchanged.
/// </summary>
public abstract class ApiTestBase : IDisposable
{
    protected const string ApiRoute = "api";
    protected const string ApiDeadLetterRoute = "api/dead-letters";

    protected readonly MsSqlContainerFixture Database;
    protected readonly ApiTestFactory Factory;

    protected ApiTestBase(MsSqlContainerFixture database)
    {
        Database = database;
        Factory = new ApiTestFactory(database);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Factory.Dispose();
    }

    protected DbContextOptions<SignalForgeDbContext> DbOptions()
        => new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseSqlServer(Database.ConnectionString)
            .Options;

    // ---------- tenant seeding (idempotent against the shared container DB) ----------

    protected static async Task EnsureTenantAsync(
        SignalForgeDbContext ctx,
        Guid tenantId,
        string tenantName,
        string apiKey,
        string? signingSecret = null)
    {
        if (!await ctx.Tenants.AnyAsync(t => t.Id == tenantId))
            ctx.Tenants.Add(Tenant.CreateWithId(tenantId, tenantName));
        if (!await ctx.ApiKeys.AnyAsync(k => k.TenantId == tenantId))
            ctx.ApiKeys.Add(ApiKey.Create(tenantId, "itest-key", apiKey));
        if (signingSecret is not null &&
            !await ctx.TenantWebhookSigningSettings.AnyAsync(s => s.TenantId == tenantId))
        {
            ctx.TenantWebhookSigningSettings.Add(TenantWebhookSigningSetting.Create(tenantId, signingSecret));
        }

        await ctx.SaveChangesAsync();
    }

    // ---------- FK-safe cleanup for any combination of tenant ids ----------

    /// <summary>
    /// Removes every row belonging to the given tenants. Children referencing NoAction parents go
    /// first (dead letters before executions, steps/versions before workflows, events/keys/settings
    /// before tenants), so a single SaveChanges runs clean under the DB's NoAction cascades.
    /// </summary>
    protected async Task CleanupAsync(params Guid[] tenantIds)
    {
        using var cleanup = new SignalForgeDbContext(DbOptions());

        foreach (var tenantId in tenantIds)
        {
            cleanup.DeadLetterMessages.RemoveRange(
                await cleanup.DeadLetterMessages.Where(d => d.TenantId == tenantId).ToListAsync());
            cleanup.OutboxMessages.RemoveRange(
                await cleanup.OutboxMessages.Where(m => m.TenantId == tenantId).ToListAsync());
            cleanup.WorkflowStepExecutions.RemoveRange(
                await cleanup.WorkflowStepExecutions
                    .Where(s => cleanup.WorkflowExecutions.Any(
                        e => e.Id == s.WorkflowExecutionId && e.TenantId == tenantId))
                    .ToListAsync());
            cleanup.WorkflowExecutions.RemoveRange(
                await cleanup.WorkflowExecutions.Where(e => e.TenantId == tenantId).ToListAsync());

            var workflows = await cleanup.Workflows.Where(w => w.TenantId == tenantId).ToListAsync();
            var workflowIds = workflows.Select(w => w.Id).ToList();
            var versions = await cleanup.WorkflowVersions
                .Where(v => workflowIds.Contains(v.WorkflowId)).ToListAsync();
            var versionIds = versions.Select(v => v.Id).ToList();
            cleanup.WorkflowSteps.RemoveRange(
                await cleanup.WorkflowSteps.Where(s => versionIds.Contains(s.WorkflowVersionId)).ToListAsync());
            cleanup.WorkflowVersions.RemoveRange(versions);
            cleanup.Workflows.RemoveRange(workflows);

            cleanup.Events.RemoveRange(
                await cleanup.Events.Where(e => e.TenantId == tenantId).ToListAsync());
            cleanup.TenantWebhookSigningSettings.RemoveRange(
                await cleanup.TenantWebhookSigningSettings.Where(s => s.TenantId == tenantId).ToListAsync());
            cleanup.ApiKeys.RemoveRange(
                await cleanup.ApiKeys.Where(k => k.TenantId == tenantId).ToListAsync());
            cleanup.Tenants.RemoveRange(
                await cleanup.Tenants.Where(t => t.Id == tenantId).ToListAsync());
        }

        await cleanup.SaveChangesAsync();
    }

    // ---------- workflow / execution seeding ----------

    protected static void SetWorkflowStepNavigation(WorkflowStepExecution execution, WorkflowStep step)
    {
        typeof(WorkflowStepExecution)
            .GetProperty(nameof(WorkflowStepExecution.WorkflowStep))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(execution, new object[] { step });
    }

    protected static async Task<(Guid WorkflowId, Guid VersionId, Guid EventId, Guid ExecutionId)> SeedRunningExecutionAsync(
        SignalForgeDbContext ctx, Guid tenantId, string workflowName)
    {
        var workflow = Workflow.Create(tenantId, workflowName, "observability workflow");
        var version = WorkflowVersion.Create(workflow.Id, 1);
        version.AddStep(1, nameof(StepType.Delay), """{"seconds":1}""", "initial");
        version.Publish();

        var evt = Event.Create(tenantId, $"obs-{Guid.NewGuid():N}", "order.created", DateTime.UtcNow, "{}");
        var execution = WorkflowExecution.Create(workflow.Id, version.Id, evt.Id, tenantId);
        execution.Start();

        ctx.Workflows.Add(workflow);
        ctx.WorkflowVersions.Add(version);
        ctx.Events.Add(evt);
        ctx.WorkflowExecutions.Add(execution);
        await ctx.SaveChangesAsync();

        return (workflow.Id, version.Id, evt.Id, execution.Id);
    }

    protected static async Task<(Guid WorkflowId, Guid DeadLetterId)> SeedStepFailureDeadLetterAsync(
        SignalForgeDbContext ctx, Guid tenantId, string workflowName, DateTime? createdAt = null)
    {
        var workflow = Workflow.Create(tenantId, workflowName, "step-failure seed workflow");
        var version = WorkflowVersion.Create(workflow.Id, 1);
        version.AddStep(1, nameof(StepType.HttpWebhook), """{"url":"http://127.0.0.1:59999"}""", "hook");
        version.Publish();

        var evt = Event.Create(tenantId, $"sdl-{Guid.NewGuid():N}", "order.created", DateTime.UtcNow, "{}");
        var execution = WorkflowExecution.Create(workflow.Id, version.Id, evt.Id, tenantId);
        execution.Start();

        var step = version.Steps.Single(s => s.StepNumber == 1);
        var stepExecution = WorkflowStepExecution.Create(execution.Id, step.Id, 1);
        SetWorkflowStepNavigation(stepExecution, step);
        stepExecution.Start();
        stepExecution.Fail("connection refused");

        var deadLetter = DeadLetterMessage.CreateFromFailedStep(stepExecution, execution);
        if (createdAt.HasValue)
            ctx.Entry(deadLetter).Property(d => d.CreatedAt).CurrentValue = createdAt.Value;

        ctx.Workflows.Add(workflow);
        ctx.WorkflowVersions.Add(version);
        ctx.Events.Add(evt);
        ctx.WorkflowExecutions.Add(execution);
        ctx.WorkflowStepExecutions.Add(stepExecution);
        ctx.DeadLetterMessages.Add(deadLetter);
        await ctx.SaveChangesAsync();

        return (workflow.Id, deadLetter.Id);
    }

    protected static async Task<(Guid WorkflowId, Guid VersionId, Guid EventId, Guid ExecutionId, Guid StepExecutionId)>
        SeedFailedStepExecutionAsync(SignalForgeDbContext ctx, Guid tenantId, string workflowName)
    {
        var workflow = Workflow.Create(tenantId, workflowName, "retry seed workflow");
        var version = WorkflowVersion.Create(workflow.Id, 1);
        version.AddStep(1, nameof(StepType.Delay), """{"seconds":1}""", "initial");
        version.Publish();

        var evt = Event.Create(tenantId, $"rtry-{Guid.NewGuid():N}", "order.created", DateTime.UtcNow, "{}");
        var execution = WorkflowExecution.Create(workflow.Id, version.Id, evt.Id, tenantId);
        execution.Start();

        var step = version.Steps.Single(s => s.StepNumber == 1);
        var stepExecution = WorkflowStepExecution.Create(execution.Id, step.Id, 1);
        stepExecution.Start();
        stepExecution.Fail("simulated failure");

        ctx.Workflows.Add(workflow);
        ctx.WorkflowVersions.Add(version);
        ctx.Events.Add(evt);
        ctx.WorkflowExecutions.Add(execution);
        ctx.WorkflowStepExecutions.Add(stepExecution);
        await ctx.SaveChangesAsync();

        return (workflow.Id, version.Id, evt.Id, execution.Id, stepExecution.Id);
    }

    // ---------- HTTP workflow-surface helpers ----------

    protected static async Task<Guid> CreateWorkflowAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync($"/{ApiRoute}/workflows", new { Name = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Guid.Parse((string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!);
    }

    protected static async Task<Guid> CreateVersionAsync(HttpClient client, Guid workflowId)
    {
        var response = await client.PostAsJsonAsync(
            $"/{ApiRoute}/workflows/{workflowId}/versions", new { Description = "v-next" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Guid.Parse((string)JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!);
    }

    protected static async Task<bool> WorkflowEnabledAsync(HttpClient client, Guid workflowId, bool expectedEnabled)
    {
        var response = await client.GetAsync($"/{ApiRoute}/workflows/{workflowId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(expectedEnabled, (bool)body["isEnabled"]!);
        return (bool)body["isEnabled"]!;
    }

    protected async Task AdvanceAsync(Guid executionId)
    {
        using var scope = Factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IWorkflowExecutionOrchestratorService>();
        var advanced = await orchestrator.AdvanceWorkflowExecutionAsync(executionId);
        Assert.True(advanced);
    }
}