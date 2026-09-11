using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.IntegrationTests;

/// <summary>
/// Regression pins for soft delete WITHOUT global query filters.
///
/// The global Tenant/Workflow filters were removed because a filter on the required end of a
/// relationship silently drops children through INNER JOINs (EF 10622). The live bug: after
/// soft-deleting a workflow, its executions vanished from the observability list (the
/// `e.Workflow.Name` projection JOINs the filtered Workflows table) while the total-count query
/// still counted them. Soft-delete is now enforced explicitly by the management/execution query
/// sites, and historical data (executions, dead-letter provenance) intentionally survives.
///
/// These tests pin the contract so the filter interaction cannot regress:
///   - executions of a deleted workflow stay observable (list + detail);
///   - dead-letter provenance survives workflow deletion;
///   - a deleted workflow is not manageable and not re-executable;
///   - a deactivated tenant's API key is rejected with 401.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class SoftDeleteQueryFilterTests : ApiTestBase
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly string KeyA = $"aA{Guid.NewGuid():N}sdTenantA";
    private static readonly string KeyB = $"bB{Guid.NewGuid():N}sdTenantB";

    public SoftDeleteQueryFilterTests(MsSqlContainerFixture database)
        : base(database)
    {
    }

    [Fact]
    public async Task DeletedWorkflow_Executions_Remain_Observable()
    {
        using var ctx = new SignalForgeDbContext(DbOptions());
        await SeedTenantsAsync(ctx);
        var (workflowId, _versionId, _eventId, executionId) = await SeedRunningExecutionAsync(ctx, TenantA, "sd-obs-wf");
        var client = Factory.CreateClient(KeyA);

        try
        {
            // Baseline: the execution is visible with its workflow name before deletion.
            var before = await GetExecutionsAsync(client, workflowId);
            Assert.Equal(1, before.TotalCount);
            var rowBefore = Assert.Single(before.Items);
            Assert.Equal(executionId, rowBefore["id"]!.AsGuid());
            Assert.Equal("sd-obs-wf", (string)rowBefore["workflowName"]!);

            // Soft-delete the workflow via the real endpoint.
            var delete = await client.DeleteAsync($"/{ApiRoute}/workflows/{workflowId}");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

            // The workflow itself is no longer manageable (explicit DeletedAt gate).
            var getDeleted = await client.GetAsync($"/{ApiRoute}/workflows/{workflowId}");
            Assert.Equal(HttpStatusCode.NotFound, getDeleted.StatusCode);

            // THE regression pin: the execution must STILL appear in the observability list,
            // name intact. With the old global filter this returned an empty items array while
            // totalCount stayed 1 (the count/items are composed differently).
            var after = await GetExecutionsAsync(client, workflowId);
            Assert.Equal(1, after.TotalCount);
            var rowAfter = Assert.Single(after.Items);
            Assert.Equal(executionId, rowAfter["id"]!.AsGuid());
            Assert.Equal("sd-obs-wf", (string)rowAfter["workflowName"]!);

            // Detail is still reachable and shows the execution state.
            var detail = await client.GetAsync($"/{ApiRoute}/workflows/{workflowId}/executions/{executionId}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var detailBody = JsonNode.Parse(await detail.Content.ReadAsStringAsync())!.AsObject();
            Assert.Equal(executionId, detailBody["id"]!.AsGuid());
        }
        finally
        {
            await CleanupAsync(TenantA, TenantB);
        }
    }

    [Fact]
    public async Task DeletedWorkflow_Is_Not_Reexecutable()
    {
        using var ctx = new SignalForgeDbContext(DbOptions());
        await SeedTenantsAsync(ctx);
        var (workflowId, versionId, eventId, _executionId) = await SeedRunningExecutionAsync(ctx, TenantA, "sd-obs-wf");
        var client = Factory.CreateClient(KeyA);

        try
        {
            var delete = await client.DeleteAsync($"/{ApiRoute}/workflows/{workflowId}");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

            var execute = await client.PostAsJsonAsync(
                $"/{ApiRoute}/workflows/{workflowId}/execute",
                new { WorkflowVersionId = versionId, EventId = eventId });
            // The orchestrator's explicit DeletedAt gate fails workflow resolution → 400.
            Assert.Equal(HttpStatusCode.BadRequest, execute.StatusCode);
        }
        finally
        {
            await CleanupAsync(TenantA, TenantB);
        }
    }

    [Fact]
    public async Task DeletedWorkflow_DeadLetter_Provenance_Survives()
    {
        using var ctx = new SignalForgeDbContext(DbOptions());
        await SeedTenantsAsync(ctx);
        var (workflowId, deadLetterId) = await SeedStepFailureDeadLetterAsync(ctx, TenantA, "sd-dl-wf");
        var client = Factory.CreateClient(KeyA);

        try
        {
            var delete = await client.DeleteAsync($"/{ApiRoute}/workflows/{workflowId}");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

            // workflowId provenance is resolved via the execution's FK column, not a Workflows
            // join, so a deleted workflow must not truncate the incident history.
            var byWorkflow = await client.GetAsync($"/{ApiDeadLetterRoute}?workflowId={workflowId}");
            Assert.Equal(HttpStatusCode.OK, byWorkflow.StatusCode);
            var byWorkflowObj = JsonNode.Parse(await byWorkflow.Content.ReadAsStringAsync())!.AsObject();
            Assert.Equal(1, (int)byWorkflowObj["totalCount"]!);
            Assert.Contains(byWorkflowObj["items"]!.AsArray(),
                dl => dl!["id"]!.AsGuid() == deadLetterId);

            var detail = await client.GetAsync($"/{ApiDeadLetterRoute}/{deadLetterId}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        }
        finally
        {
            await CleanupAsync(TenantA, TenantB);
        }
    }

    [Fact]
    public async Task DeactivatedTenant_ApiKey_Is_Rejected()
    {
        using var ctx = new SignalForgeDbContext(DbOptions());
        await SeedTenantsAsync(ctx);

        var tenant = await ctx.Tenants.SingleAsync(t => t.Id == TenantB);
        tenant.Deactivate();
        await ctx.SaveChangesAsync();

        var client = Factory.CreateClient(KeyB);
        var response = await client.GetAsync("/TestAuth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- helpers ----------

    private static async Task SeedTenantsAsync(SignalForgeDbContext ctx)
    {
        // Idempotent against leftovers in the shared container DB (the suite's established pattern).
        await EnsureTenantAsync(ctx, TenantA, "itest-sd-tenant-a", KeyA);
        await EnsureTenantAsync(ctx, TenantB, "itest-sd-tenant-b", KeyB);
    }

    private static async Task<(int TotalCount, List<JsonObject> Items)> GetExecutionsAsync(
        HttpClient client, Guid workflowId)
    {
        var response = await client.GetAsync($"/{ApiRoute}/executions?workflowId={workflowId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        return (
            (int)body["totalCount"]!,
            body["items"]!.AsArray().Select(n => n!.AsObject()).ToList());
    }
}