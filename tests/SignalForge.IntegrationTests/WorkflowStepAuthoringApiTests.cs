using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.IntegrationTests;

/// <summary>
/// HTTP-level coverage for the C2 step-authoring surface: add/update/reorder/enable/disable on
/// draft versions, config validation at the wire, and the published-version immutability rule.
/// </summary>
[Collection(MsSqlCollection.Name)]
public class WorkflowStepAuthoringApiTests : ApiTestBase
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly string ApiKey = $"c2s{Guid.NewGuid():N}tenant";

    public WorkflowStepAuthoringApiTests(MsSqlContainerFixture database)
        : base(database)
    {
    }

    private async Task<(HttpClient Client, Guid WorkflowId, Guid VersionId, Guid StepId)> SeedWorkflowWithStepAsync()
    {
        using var ctx = new SignalForgeDbContext(DbOptions());
        await EnsureTenantAsync(ctx, TenantId, "itest-step-authoring", ApiKey);

        var workflowId = await CreateWorkflowAsync(Factory.CreateClient(ApiKey), "authoring-wf");
        var versionId = await CreateVersionAsync(Factory.CreateClient(ApiKey), workflowId);

        var client = Factory.CreateClient(ApiKey);
        var add = await client.PostAsJsonAsync(
            $"/{ApiRoute}/workflows/{workflowId}/versions/{versionId}/steps",
            new { StepNumber = 1, StepType = "LogAudit", Configuration = """{"message":"first"}""", Name = "audit" });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var stepId = Guid.Parse((string)JsonNode.Parse(await add.Content.ReadAsStringAsync())!["id"]!);

        return (client, workflowId, versionId, stepId);
    }

    [Fact]
    public async Task AddStep_Returns_201_And_Persists()
    {
        try
        {
            var (client, workflowId, versionId, stepId) = await SeedWorkflowWithStepAsync();

            var added = await client.PostAsJsonAsync(
                $"/{ApiRoute}/workflows/{workflowId}/versions/{versionId}/steps",
                new { StepNumber = 2, StepType = "Delay", Configuration = """{"seconds":5}""", Description = "wait a bit" });
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
            var body = JsonNode.Parse(await added.Content.ReadAsStringAsync())!.AsObject();
            Assert.Equal(2, (int)body["stepNumber"]!);
            Assert.Equal("Delay", (string)body["stepType"]!);
            Assert.True((bool)body["isEnabled"]!);

            using var verify = new SignalForgeDbContext(DbOptions());
            Assert.Equal(2, await verify.WorkflowSteps.CountAsync(s => s.WorkflowVersionId == versionId));
            Assert.True(await verify.WorkflowSteps.AnyAsync(s => s.Id == stepId));
        }
        finally
        {
            await CleanupAsync(TenantId);
        }
    }

    [Fact]
    public async Task AddStep_With_Invalid_Configuration_Returns_400()
    {
        try
        {
            var (client, workflowId, versionId, _) = await SeedWorkflowWithStepAsync();

            var added = await client.PostAsJsonAsync(
                $"/{ApiRoute}/workflows/{workflowId}/versions/{versionId}/steps",
                new { StepNumber = 2, StepType = "Delay", Configuration = """{"notSeconds":5}""" });
            Assert.Equal(HttpStatusCode.BadRequest, added.StatusCode);
        }
        finally
        {
            await CleanupAsync(TenantId);
        }
    }

    [Fact]
    public async Task UpdateStep_Rewrites_Configuration_And_Toggles_Enabled()
    {
        try
        {
            var (client, workflowId, versionId, stepId) = await SeedWorkflowWithStepAsync();

            var updated = await client.PutAsJsonAsync(
                $"/{ApiRoute}/workflows/{workflowId}/versions/{versionId}/steps/{stepId}",
                new { Configuration = """{"message":"rewritten"}""", IsEnabled = false });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var body = JsonNode.Parse(await updated.Content.ReadAsStringAsync())!.AsObject();
            Assert.False((bool)body["isEnabled"]!);
            Assert.Contains("rewritten", (string)body["configuration"]!);

            var disabled = await client.PostAsync(
                $"/{ApiRoute}/workflows/{workflowId}/versions/{versionId}/steps/{stepId}/enable", null);
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        }
        finally
        {
            await CleanupAsync(TenantId);
        }
    }

    [Fact]
    public async Task Delete_Then_Renumber_Leaves_Contiguous_StepNumbers()
    {
        try
        {
            var (client, workflowId, versionId, firstStepId) = await SeedWorkflowWithStepAsync();

            var second = await client.PostAsJsonAsync(
                $"/{ApiRoute}/workflows/{workflowId}/versions/{versionId}/steps",
                new { StepNumber = 2, StepType = "LogAudit", Configuration = """{"message":"second"}""" });
            var secondId = Guid.Parse((string)JsonNode.Parse(await second.Content.ReadAsStringAsync())!["id"]!);

            var deleted = await client.DeleteAsync(
                $"/{ApiRoute}/workflows/{workflowId}/versions/{versionId}/steps/{firstStepId}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

            var reordered = await client.PutAsJsonAsync(
                $"/{ApiRoute}/workflows/{workflowId}/versions/{versionId}/steps/reorder",
                new { StepIdsInOrder = new[] { secondId } });
            Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);
            var steps = JsonNode.Parse(await reordered.Content.ReadAsStringAsync())!.AsObject()["steps"]!.AsArray();
            Assert.Single(steps);
            Assert.Equal(1, (int)steps[0]!["stepNumber"]!);
            Assert.Equal(secondId.ToString(), (string)steps[0]!["id"]!);
        }
        finally
        {
            await CleanupAsync(TenantId);
        }
    }

    [Fact]
    public async Task Actions_On_Other_Tenants_Workflow_Are_NotFound()
    {
        try
        {
            using var ctx = new SignalForgeDbContext(DbOptions());
            await EnsureTenantAsync(ctx, TenantId, "itest-step-authoring", ApiKey);

            var foreign = Guid.NewGuid();
            await EnsureTenantAsync(ctx, foreign, "itest-step-authoring-foreign", $"{foreign}");

            var (client, workflowId, versionId, _) = await SeedWorkflowWithStepAsync();
            var foreignClient = Factory.CreateClient($"{foreign}");

            var added = await foreignClient.PostAsJsonAsync(
                $"/{ApiRoute}/workflows/{workflowId}/versions/{versionId}/steps",
                new { StepNumber = 2, StepType = "Delay", Configuration = """{"seconds":1}""" });
            Assert.Equal(HttpStatusCode.NotFound, added.StatusCode);
        }
        finally
        {
            await CleanupAsync(TenantId);
        }
    }

    [Fact]
    public async Task Mutations_On_Published_Version_Return_400()
    {
        try
        {
            var (client, workflowId, versionId, stepId) = await SeedWorkflowWithStepAsync();

            using var ctx = new SignalForgeDbContext(DbOptions());
            var workflow = await ctx.Workflows
                .Include(w => w.Versions)
                    .ThenInclude(v => v.Steps)
                .SingleAsync(w => w.Id == workflowId);
            workflow.Enable();
            workflow.Versions.Single(v => v.Id == versionId).Publish();
            await ctx.SaveChangesAsync();

            var added = await client.PostAsJsonAsync(
                $"/{ApiRoute}/workflows/{workflowId}/versions/{versionId}/steps",
                new { StepNumber = 2, StepType = "Delay", Configuration = """{"seconds":1}""" });
            Assert.Equal(HttpStatusCode.BadRequest, added.StatusCode);

            var deleted = await client.DeleteAsync(
                $"/{ApiRoute}/workflows/{workflowId}/versions/{versionId}/steps/{stepId}");
            Assert.Equal(HttpStatusCode.BadRequest, deleted.StatusCode);
        }
        finally
        {
            await CleanupAsync(TenantId);
        }
    }
}