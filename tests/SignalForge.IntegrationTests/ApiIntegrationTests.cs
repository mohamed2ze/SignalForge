using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.IntegrationTests;

/// <summary>
/// End-to-end HTTP tests against the real API hosted by WebApplicationFactory, backed by a
/// real SQL Server (Testcontainers). Exercises the whole pipeline: API-key auth middleware,
/// controllers, application services, EF persistence, and the domain model — no mocks.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class ApiIntegrationTests : ApiTestBase
{
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly string KeyB = $"bB{Guid.NewGuid():N}TenantB";
    private const string SecretB = "sfTestK2-tenant-b-signing-secret";

    public ApiIntegrationTests(MsSqlContainerFixture database)
        : base(database)
    {
    }

    // ---------- API-key authentication ----------

    [Fact]
    public async Task NoApiKey_Returns_401()
    {
        var client = Factory.CreateClient();

        var response = await client.GetAsync("/TestAuth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InvalidApiKey_Returns_401()
    {
        var client = Factory.CreateClient("definitely-not-a-real-key-123456");

        var response = await client.GetAsync("/TestAuth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SeededApiKey_Authenticates_To_Seeded_Tenant()
    {
        var client = Factory.CreateClientForSeededTenant();

        var response = await client.GetAsync("/TestAuth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Content.Headers.ContentType?.MediaType == "application/json");

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.True((bool)body["authenticated"]!);
        Assert.Equal(ApiTestFactory.TenantId.ToString(), (string)body["tenantId"]!);
        Assert.Equal(ApiTestFactory.ApiKeyName, (string)body["apiKeyName"]!);
    }

    // ---------- Event ingestion + idempotency ----------

    [Fact]
    public async Task IngestEvent_Returns201_Then_Idempotent_200_WithSameId()
    {
        var client = Factory.CreateClientForSeededTenant();
        var externalId = $"ext-{Guid.NewGuid():N}";

        var createPayload = new
        {
            ExternalEventId = externalId,
            EventType = "order.created",
            Payload = "{\"orderId\":42}"
        };
        var createRaw = EventSigner.CamelJson(createPayload);

        var first = await EventSigner.PostSignedEventAsync(client, ApiTestFactory.SigningSecret, createRaw);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = JsonNode.Parse(await first.Content.ReadAsStringAsync())!.AsObject();
        var eventId = Guid.Parse((string)firstBody["id"]!);
        Assert.Equal(externalId, (string)firstBody["externalEventId"]!);

        var duplicate = await EventSigner.PostSignedEventAsync(client, ApiTestFactory.SigningSecret, createRaw);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        var duplicateBody = JsonNode.Parse(await duplicate.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(eventId, Guid.Parse((string)duplicateBody["id"]!));

        var getById = await client.GetAsync($"/{ApiRoute}/events/{eventId}");
        Assert.Equal(HttpStatusCode.OK, getById.StatusCode);
        Assert.Equal(eventId, Guid.Parse((string)JsonNode.Parse(await getById.Content.ReadAsStringAsync())!["id"]!));
    }

    [Fact]
    public async Task IngestEvent_Missing_ExternalId_Returns_400()
    {
        var client = Factory.CreateClientForSeededTenant();

        var rawMissingExternalId = EventSigner.CamelJson(new
        {
            EventType = "order.created",
            Payload = "{}"
        });

        var response = await EventSigner.PostSignedEventAsync(client, ApiTestFactory.SigningSecret, rawMissingExternalId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- Webhook signature verification (Decision #23) ----------

    [Fact]
    public async Task IngestEvent_WithoutSignature_Returns_401()
    {
        var client = Factory.CreateClientForSeededTenant();
        var rawJson = EventSigner.CamelJson(new
        {
            ExternalEventId = $"ext-unsigned-{Guid.NewGuid():N}",
            EventType = "order.created",
            Payload = "{}"
        });

        using var content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"/{ApiRoute}/events", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IngestEvent_WithTamperedBody_Returns_401()
    {
        var client = Factory.CreateClientForSeededTenant();
        var rawJson = EventSigner.CamelJson(new
        {
            ExternalEventId = $"ext-tampered-{Guid.NewGuid():N}",
            EventType = "order.created",
            Payload = "{}"
        });
        // Flip a route-relevant value so the transmitted bytes differ from the signed bytes.
        var tampered = rawJson.Replace("ext-tampered", "Ext-tAMPERED");

        using var content = new StringContent(tampered, System.Text.Encoding.UTF8, "application/json");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        content.Headers.Add(SignalForge.Application.Security.EventSignatureVerifier.TimestampHeader, timestamp.ToString());
        content.Headers.Add(SignalForge.Application.Security.EventSignatureVerifier.SignatureHeader,
            SignalForge.Application.Security.EventSignatureVerifier.ComputeSignature(
                ApiTestFactory.SigningSecret, timestamp, rawJson));

        var response = await client.PostAsync($"/{ApiRoute}/events", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IngestEvent_WithWrongSecret_Returns_401()
    {
        var client = Factory.CreateClientForSeededTenant();
        var rawJson = EventSigner.CamelJson(new
        {
            ExternalEventId = $"ext-wrong-secret-{Guid.NewGuid():N}",
            EventType = "order.created",
            Payload = "{}"
        });

        using var content = EventSigner.SignedContent("not-the-real-secret", rawJson);
        var response = await client.PostAsync($"/{ApiRoute}/events", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IngestEvent_WithStaleTimestamp_Returns_401()
    {
        var client = Factory.CreateClientForSeededTenant();
        var rawJson = EventSigner.CamelJson(new
        {
            ExternalEventId = $"ext-stale-{Guid.NewGuid():N}",
            EventType = "order.created",
            Payload = "{}"
        });
        var staleTimestamp = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();

        using var content = EventSigner.SignedContent(ApiTestFactory.SigningSecret, rawJson, staleTimestamp);
        var response = await client.PostAsync($"/{ApiRoute}/events", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IngestEvent_SignedWithTenantBSecret_OnTenantA_Returns_401()
    {
        var client = Factory.CreateClientForSeededTenant();
        await SeedTenantBAsync();
        var rawJson = EventSigner.CamelJson(new
        {
            ExternalEventId = $"ext-x-tenant-{Guid.NewGuid():N}",
            EventType = "order.created",
            Payload = "{}"
        });

        using var content = EventSigner.SignedContent(SecretB, rawJson);
        var response = await client.PostAsync($"/{ApiRoute}/events", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- Cross-tenant isolation over HTTP ----------

    [Fact]
    public async Task CrossTenant_Events_Are_Isolated_Over_Http()
    {
        var clientA = Factory.CreateClientForSeededTenant();
        await SeedTenantBAsync();

        var clientB = Factory.CreateClient(KeyB);
        var externalId = $"ext-cross-{Guid.NewGuid():N}";

        // Tenant A ingests.
        var createdA = await EventSigner.PostSignedEventAsync(clientA, ApiTestFactory.SigningSecret,
            EventSigner.CamelJson(new
            {
                ExternalEventId = externalId,
                EventType = "order.created",
                Payload = "{\"orderId\":1}"
            }));
        Assert.Equal(HttpStatusCode.Created, createdA.StatusCode);
        var eventIdA = Guid.Parse((string)JsonNode.Parse(await createdA.Content.ReadAsStringAsync())!["id"]!);

        // Tenant B cannot read A's event even with its ID.
        var crossRead = await clientB.GetAsync($"/{ApiRoute}/events/{eventIdA}");
        Assert.Equal(HttpStatusCode.NotFound, crossRead.StatusCode);

        // The same external id is a NEW event in tenant B's namespace.
        var createdB = await EventSigner.PostSignedEventAsync(clientB, SecretB,
            EventSigner.CamelJson(new
            {
                ExternalEventId = externalId,
                EventType = "order.created",
                Payload = "{\"orderId\":2}"
            }));
        Assert.Equal(HttpStatusCode.Created, createdB.StatusCode);
        var eventIdB = Guid.Parse((string)JsonNode.Parse(await createdB.Content.ReadAsStringAsync())!["id"]!);
        Assert.NotEqual(eventIdA, eventIdB);
    }

    // ---------- Workflow lifecycle E2E ----------

    [Fact]
    public async Task Publish_Version_Without_Steps_Returns_400()
    {
        var client = Factory.CreateClientForSeededTenant();

        var workflow = await CreateWorkflowAsync(client, "empty-wf");
        var version = await CreateVersionAsync(client, workflow);

        var publish = await client.PostAsync(
            $"/{ApiRoute}/workflows/{workflow}/versions/{version}/publish", null);
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
    }

    [Fact]
    public async Task WorkflowLifecycle_EndToEnd_Over_Http()
    {
        var client = Factory.CreateClientForSeededTenant();

        // 1. Create workflow (draft version 1).
        var workflow = await CreateWorkflowAsync(client, "itest-e2e");
        await WorkflowEnabledAsync(client, workflow, expectedEnabled: false);

        // 2. Create version 2, add a step at the DB layer (no HTTP step-authoring endpoint yet),
        //    then publish — this is the publish guards' happy path.
        var version = await CreateVersionAsync(client, workflow);

        using (var ctx = new SignalForgeDbContext(DbOptions()))
        {
            var draft = await ctx.WorkflowVersions.SingleAsync(v => v.Id == version);
            draft.AddStep(1, "Delay", "{\"seconds\":1}", "initial", "gate step");
            await ctx.SaveChangesAsync();
        }

        var publish = await client.PostAsync(
            $"/{ApiRoute}/workflows/{workflow}/versions/{version}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var published = JsonNode.Parse(await publish.Content.ReadAsStringAsync())!.AsObject();
        Assert.True((bool)published["isPublished"]!);
        await WorkflowEnabledAsync(client, workflow, expectedEnabled: true);

        // 3. Ingest the triggering event.
        var ingest = await EventSigner.PostSignedEventAsync(client, ApiTestFactory.SigningSecret,
            EventSigner.CamelJson(new
            {
                ExternalEventId = $"ext-e2e-{Guid.NewGuid():N}",
                EventType = "order.created",
                Payload = "{\"orderId\":1}"
            }));
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);
        var eventId = Guid.Parse((string)JsonNode.Parse(await ingest.Content.ReadAsStringAsync())!["id"]!);

        // 4. Execute the workflow.
        var execute = await client.PostAsJsonAsync(
            $"/{ApiRoute}/workflows/{workflow}/execute",
            new { WorkflowVersionId = version, EventId = eventId });
        Assert.Equal(HttpStatusCode.Created, execute.StatusCode);
        var execution = JsonNode.Parse(await execute.Content.ReadAsStringAsync())!.AsObject();
        var executionId = Guid.Parse((string)execution["id"]!);
        Assert.Equal("Running", (string)execution["status"]!);
        Assert.Equal(0, (int)execution["currentStepNumber"]!);

        // 5. No step executions exist yet (the worker drives advancement, not the API request).
        var stepsBefore = await client.GetAsync($"/{ApiRoute}/workflows/{executionId}/steps");
        Assert.Equal(HttpStatusCode.OK, stepsBefore.StatusCode);
        var stepsBeforeArr = JsonNode.Parse(await stepsBefore.Content.ReadAsStringAsync())!.AsArray();
        Assert.Empty(stepsBeforeArr);

        // 6. Drive one worker tick in-process (AdvanceWorkflowExecutionAsync), then verify via HTTP.
        await AdvanceAsync(executionId);

        var stepsAfter = await client.GetAsync($"/{ApiRoute}/workflows/{executionId}/steps");
        Assert.Equal(HttpStatusCode.OK, stepsAfter.StatusCode);
        var stepsAfterArr = JsonNode.Parse(await stepsAfter.Content.ReadAsStringAsync())!.AsArray();
        Assert.Single(stepsAfterArr);
        Assert.Equal("Succeeded", (string)stepsAfterArr[0]!["status"]!);
        Assert.Equal(1, (int)stepsAfterArr[0]!["stepNumber"]!);

        // 7. One more tick completes the execution (no remaining steps).
        await AdvanceAsync(executionId);

        var executionAfter = await client.GetAsync($"/{ApiRoute}/workflows/{workflow}/executions/{executionId}");
        Assert.Equal(HttpStatusCode.OK, executionAfter.StatusCode);
        var executionAfterObj = JsonNode.Parse(await executionAfter.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("Succeeded", (string)executionAfterObj["status"]!);
    }

    [Fact]
    public async Task CrossTenant_Workflow_Not_Found_Over_Http()
    {
        var clientA = Factory.CreateClientForSeededTenant();
        await SeedTenantBAsync();

        var workflow = await CreateWorkflowAsync(clientA, "secret-wf");

        var clientB = Factory.CreateClient(KeyB);
        var crossRead = await clientB.GetAsync($"/{ApiRoute}/workflows/{workflow}");
        Assert.Equal(HttpStatusCode.NotFound, crossRead.StatusCode);
    }

    // ---------- Dead-letter lifecycle ----------

    [Fact]
    public async Task DeadLetter_Lifecycle_Over_Http()
    {
        var client = Factory.CreateClientForSeededTenant();
        await SeedTenantBAsync();

        var deadLetterId = await SeedDeadLetterAsync();

        // List (Level 5 paged envelope)
        var list = await client.GetAsync($"/{ApiRoute}/deadLetter");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listObj = JsonNode.Parse(await list.Content.ReadAsStringAsync())!.AsObject();
        var listArr = listObj["items"]!.AsArray();
        Assert.Equal(1, (int)listObj["page"]!);
        Assert.Contains(listArr, dl => Guid.Parse((string)dl!["id"]!) == deadLetterId);

        // Detail
        var detail = await client.GetAsync($"/{ApiRoute}/deadLetter/{deadLetterId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailObj = JsonNode.Parse(await detail.Content.ReadAsStringAsync())!.AsObject();
        Assert.False((bool)detailObj["isProcessed"]!);
        Assert.Equal("Test/Fail", (string)detailObj["originalMessageType"]!);

        // Counts before processing
        var countsBefore = await client.GetAsync($"/{ApiRoute}/deadLetter/counts");
        Assert.Equal(HttpStatusCode.OK, countsBefore.StatusCode);
        var countsBeforeObj = JsonNode.Parse(await countsBefore.Content.ReadAsStringAsync())!.AsObject();
        Assert.True((int)countsBeforeObj["unprocessed"]! >= 1);

        // Mark processed
        var process = await client.PostAsync($"/{ApiRoute}/deadLetter/{deadLetterId}/process", null);
        Assert.Equal(HttpStatusCode.OK, process.StatusCode);
        Assert.True(bool.Parse(await process.Content.ReadAsStringAsync()));

        // Excluded from the unprocessed-only list, reflected in counts.
        var unprocessedOnly = await client.GetAsync($"/{ApiRoute}/deadLetter?onlyUnprocessed=true");
        var unprocessedObj = JsonNode.Parse(await unprocessedOnly.Content.ReadAsStringAsync())!.AsObject();
        var unprocessedArr = unprocessedObj["items"]!.AsArray();
        Assert.DoesNotContain(unprocessedArr, dl => Guid.Parse((string)dl!["id"]!) == deadLetterId);

        var countsAfter = await client.GetAsync($"/{ApiRoute}/deadLetter/counts");
        var countsAfterObj = JsonNode.Parse(await countsAfter.Content.ReadAsStringAsync())!.AsObject();
        Assert.True((int)countsAfterObj["processed"]! >= 1);

        // Cross-tenant isolation: tenant B cannot see or process A's dead letter.
        var clientB = Factory.CreateClient(KeyB);
        var crossRead = await clientB.GetAsync($"/{ApiRoute}/deadLetter/{deadLetterId}");
        Assert.Equal(HttpStatusCode.NotFound, crossRead.StatusCode);
        var crossProcess = await clientB.PostAsync($"/{ApiRoute}/deadLetter/{deadLetterId}/process", null);
        Assert.Equal(HttpStatusCode.NotFound, crossProcess.StatusCode);
    }

    [Fact]
    public async Task DeadLetter_Counts_Require_Auth()
    {
        var anonymous = Factory.CreateClient();

        var counts = await anonymous.GetAsync($"/{ApiRoute}/deadLetter/counts");
        Assert.Equal(HttpStatusCode.Unauthorized, counts.StatusCode);
    }

    // ---------- helpers ----------

    private async Task SeedTenantBAsync()
    {
        using var ctx = new SignalForgeDbContext(DbOptions());
        await EnsureTenantAsync(ctx, TenantB, "itest-tenant-b", KeyB, SecretB);
    }

    private async Task<Guid> SeedDeadLetterAsync()
    {
        using var ctx = new SignalForgeDbContext(DbOptions());
        var outbox = OutboxMessage.Create(ApiTestFactory.TenantId, "Test/Fail", "{\"boom\":true}");
        var deadLetter = DeadLetterMessage.CreateFromOutboxMessage(outbox, "simulated failure");
        ctx.OutboxMessages.Add(outbox);
        ctx.DeadLetterMessages.Add(deadLetter);
        await ctx.SaveChangesAsync();
        return deadLetter.Id;
    }
}