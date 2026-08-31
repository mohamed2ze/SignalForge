using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.IntegrationTests;

/// <summary>
/// Integration tests for the execution observability surface (Stage 5, Level 4): paged/filtered
/// execution history, server-side aggregates (status counts, step latency, failure causes), and
/// cross-tenant isolation. A deterministic execution history is seeded once per run against the
/// shared container DB using domain objects; private-set timestamps/statuses are written through
/// the EF change tracker so aggregate values are exact rather than timing-dependent.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class ExecutionsObservabilityTests : IDisposable
{
    private const string WfAName = "obs-wf-a";
    private const string WfBName = "obs-wf-b";

    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly string KeyB = $"bB{Guid.NewGuid():N}TenantB";

    private readonly MsSqlContainerFixture _database;
    private readonly ApiTestFactory _factory;

    private Guid _wfAId;
    private Guid _wfBId;
    private DateTime _seedUtc;
    private bool _seeded;

    public ExecutionsObservabilityTests(MsSqlContainerFixture database)
    {
        _database = database;
        _factory = new ApiTestFactory(database);
    }

    public void Dispose() => _factory.Dispose();

    private DbContextOptions<SignalForgeDbContext> DbOptions()
        => new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseSqlServer(_database.ConnectionString)
            .Options;

    [Fact]
    public async Task Executions_List_Filtered_Paged()
    {
        await SeedHistoryAsync();
        var client = _factory.CreateClientForSeededTenant();

        // All of tenant A's wfA history (exact: other test classes can't touch our workflows).
        var all = JsonNode.Parse(await client
            .GetStringAsync($"/api/executions?workflowId={_wfAId}&pageSize=100"))!.AsObject();
        Assert.Equal(3, (int)all["totalCount"]!);
        Assert.Equal(3, all["items"]!.AsArray().Count);

        // Order must match the server's StartedAt-descending sort, projected from the DB.
        using (var ctx = new SignalForgeDbContext(DbOptions()))
        {
            var expectedOrder = await ctx.WorkflowExecutions
                .Where(e => e.WorkflowId == _wfAId)
                .OrderByDescending(e => e.StartedAt)
                .Select(e => e.Id)
                .ToListAsync();
            var items = all["items"]!.AsArray();
            Assert.Equal(expectedOrder.Count, items.Count);
            for (var i = 0; i < items.Count; i++)
                Assert.Equal(expectedOrder[i], Guid.Parse((string)items[i]!["id"]!));

            // The succeeded execution has workflow/version context joined in.
            var succeeded = items.Single(i => (string)i!["status"]! == WorkflowExecutionStatus.Succeeded);
            Assert.Equal(WfAName, (string)succeeded!["workflowName"]!);
            Assert.Equal(1, (int)succeeded["workflowVersionNumber"]!);
        }

        // Status filter: the failed execution belongs to wfB, so wfA + Failed is empty.
        var failedWfA = JsonNode.Parse(await client
            .GetStringAsync($"/api/executions?workflowId={_wfAId}&status=Failed"))!.AsObject();
        Assert.Equal(0, (int)failedWfA["totalCount"]!);

        var failedAll = JsonNode.Parse(await client
            .GetStringAsync($"/api/executions?status=Failed"))!.AsObject();
        Assert.True((int)failedAll["totalCount"]! >= 1);

        // Paging: pageSize=2 -> page1 has 2, page2 has 1, totalCount stays 3.
        var page1 = JsonNode.Parse(await client
            .GetStringAsync($"/api/executions?workflowId={_wfAId}&page=1&pageSize=2"))!.AsObject();
        Assert.Equal(3, (int)page1["totalCount"]!);
        Assert.Collection(page1["items"]!.AsArray(), _ => { }, _ => { });

        var page2 = JsonNode.Parse(await client
            .GetStringAsync($"/api/executions?workflowId={_wfAId}&page=2&pageSize=2"))!.AsObject();
        Assert.Single(page2["items"]!.AsArray());
    }

    [Fact]
    public async Task Executions_TimeWindow_Filters()
    {
        await SeedHistoryAsync();
        var client = _factory.CreateClientForSeededTenant();

        // The failed execution is 2h old -> outside a 15-minute window; the rest are within.
        var from = _seedUtc.AddHours(-2).AddSeconds(1);
        var to = _seedUtc.AddSeconds(1);
        var windowed = JsonNode.Parse(await client
            .GetStringAsync($"/api/executions?workflowId={_wfAId}&from={from:O}&to={to:O}"))!.AsObject();
        Assert.Equal(3, (int)windowed["totalCount"]!);

        // A narrow window between the running (5 min) and succeeded (10 min) executions.
        var fromNarrow = _seedUtc.AddMinutes(-7);
        var toNarrow = _seedUtc.AddMinutes(-3);
        var narrow = JsonNode.Parse(await client
            .GetStringAsync($"/api/executions?workflowId={_wfAId}&from={fromNarrow:O}&to={toNarrow:O}"))!.AsObject();
        Assert.Equal(1, (int)narrow["totalCount"]!);
        using (var ctx = new SignalForgeDbContext(DbOptions()))
        {
            var runningId = await ctx.WorkflowExecutions
                .Where(e => e.WorkflowId == _wfAId && e.Status == WorkflowExecutionStatus.Running)
                .Select(e => e.Id)
                .SingleAsync();
            Assert.Equal(runningId, Guid.Parse((string)narrow["items"]![0]!["id"]!));
        }
    }

    [Fact]
    public async Task Aggregates_ReturnStatusCounts_AndStepLatency()
    {
        await SeedHistoryAsync();
        var client = _factory.CreateClientForSeededTenant();

        var aggregates = JsonNode.Parse(await client
            .GetStringAsync($"/api/executions/aggregates?workflowId={_wfAId}"))!.AsObject();

        // Status counts for wfA: succeeded(1), running(1), retrying(1); no failed.
        var statusCounts = ToCounts(aggregates["statusCounts"]!.AsArray());
        Assert.Equal(1, statusCounts[WorkflowExecutionStatus.Succeeded]);
        Assert.Equal(1, statusCounts[WorkflowExecutionStatus.Running]);
        Assert.Equal(1, statusCounts[WorkflowExecutionStatus.Retrying]);
        Assert.False(statusCounts.ContainsKey(WorkflowExecutionStatus.Failed));

        // Step latency across the two completed-step wfA executions:
        //   LogAudit 500ms (exec1 step1) + 300ms (exec2 step1) => min/avg/max 300/400/500
        //   Delay 1000ms (exec1 step2), NotificationSimulation 200ms (exec1 step3).
        var latency = aggregates["stepLatency"]!.AsArray()
            .Select(row => (
                StepType: (string)row!["stepType"]!,
                Count: (int)row["count"]!,
                AverageMs: (int)row["averageMs"]!,
                MinMs: (int)row["minMs"]!,
                MaxMs: (int)row["maxMs"]!))
            .ToDictionary(x => x.StepType, x => x);

        Assert.Equal(2, latency["LogAudit"].Count);
        Assert.Equal(300, latency["LogAudit"].MinMs);
        Assert.Equal(400, latency["LogAudit"].AverageMs);
        Assert.Equal(500, latency["LogAudit"].MaxMs);
        Assert.Equal(1, latency["Delay"].Count);
        Assert.Equal(1000, latency["Delay"].MinMs);
        Assert.Equal(1000, latency["Delay"].MaxMs);
        Assert.Equal(1, latency["NotificationSimulation"].Count);
        Assert.Equal(200, latency["NotificationSimulation"].MinMs);

        // Failure summary scoped to wfA: the failed execution is wfB, so nothing here.
        var failuresA = aggregates["failures"]!.AsObject();
        Assert.Equal(0, (int)failuresA["failedExecutionCount"]!);
        Assert.Empty(failuresA["executionFailureCauses"]!.AsArray());
        Assert.Empty(failuresA["stepFailureCauses"]!.AsArray());
    }

    [Fact]
    public async Task Aggregates_IncludeFailedExecutionCauses_AndRetries()
    {
        await SeedHistoryAsync();
        var client = _factory.CreateClientForSeededTenant();

        var aggregates = JsonNode.Parse(await client
            .GetStringAsync($"/api/executions/aggregates?workflowId={_wfBId}"))!.AsObject();

        var statusCounts = ToCounts(aggregates["statusCounts"]!.AsArray());
        Assert.Equal(1, statusCounts[WorkflowExecutionStatus.Failed]);

        var failures = aggregates["failures"]!.AsObject();
        Assert.Equal(1, (int)failures["failedExecutionCount"]!);
        Assert.Equal(1, (int)failures["retriedExecutionCount"]!);
        Assert.Equal(1, (int)failures["totalStepRetries"]!);

        var executionCauses = failures["executionFailureCauses"]!.AsArray();
        var stepCauses = failures["stepFailureCauses"]!.AsArray();
        Assert.Contains(executionCauses, c => (string)c!["cause"]! == "execution boom" &&
                                              (int)c["count"]! == 1);
        Assert.Contains(stepCauses, c => (string)c!["cause"]! == "connection refused" &&
                                         (int)c["count"]! == 1);
    }

    [Fact]
    public async Task Observability_CrossTenant_Isolation()
    {
        await SeedHistoryAsync();
        await SeedTenantBAsync();
        var clientA = _factory.CreateClientForSeededTenant();
        var clientB = _factory.CreateClient(KeyB);

        var fromA = await clientA.GetAsync($"/api/executions?workflowId={_wfAId}");
        Assert.Equal(HttpStatusCode.OK, fromA.StatusCode);
        Assert.True((int)JsonNode.Parse(await fromA.Content.ReadAsStringAsync())!["totalCount"]! >= 1);

        // Tenant B must never see A's executions, even when querying with A's workflow id.
        var fromB = await clientB.GetAsync($"/api/executions?workflowId={_wfAId}");
        Assert.Equal(HttpStatusCode.OK, fromB.StatusCode);
        Assert.Equal(0, (int)JsonNode.Parse(await fromB.Content.ReadAsStringAsync())!["totalCount"]!);

        var aggB = await clientB.GetAsync($"/api/executions/aggregates?workflowId={_wfAId}");
        Assert.Equal(HttpStatusCode.OK, aggB.StatusCode);
        var aggBody = JsonNode.Parse(await aggB.Content.ReadAsStringAsync())!.AsObject();
        Assert.Empty(aggBody["statusCounts"]!.AsArray());
        Assert.Empty(aggBody["stepLatency"]!.AsArray());
        Assert.Equal(0, (int)aggBody["failures"]!["failedExecutionCount"]!);
    }

    [Theory]
    [InlineData("?status=Bogus")]
    [InlineData("?from=not-a-date")]
    public async Task Executions_InvalidFilters_Return400(string query)
    {
        await SeedHistoryAsync();
        var client = _factory.CreateClientForSeededTenant();

        var response = await client.GetAsync($"/api/executions{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Dictionary<string, int> ToCounts(JsonArray array)
        => array.Select(row => ((string)row!["status"]!, (int)row["count"]!))
            .ToDictionary(x => x.Item1, x => x.Item2);

    private async Task SeedHistoryAsync()
    {
        if (_seeded)
            return;

        // Idempotent within the run (shared container DB): seed only once.
        using var probe = new SignalForgeDbContext(DbOptions());
        var existing = await probe.Workflows.FirstOrDefaultAsync(w => w.Name == WfAName);
        if (existing != null)
        {
            // Reload derived state from the DB seeded by an earlier test instance. The oldest of the
            // four executions was seeded at seedUtc - 10 minutes (execution 1), so recover seedUtc.
            _wfAId = existing.Id;
            _wfBId = (await probe.Workflows.SingleAsync(w => w.Name == WfBName)).Id;
            var earliest = await probe.WorkflowExecutions
                .Where(e => e.WorkflowId == _wfAId)
                .MinAsync(e => e.StartedAt);
            _seedUtc = earliest.AddMinutes(10);
            _seeded = true;
            return;
        }

        var seedUtc = DateTime.UtcNow;
        var tenantId = ApiTestFactory.TenantId;

        // The API-host's startup seeder may not have run yet for this test instance, so the tenant
        // row must exist before workflows/events can reference it (FK_Workflows_Tenants_TenantId).
        using var ctx = new SignalForgeDbContext(DbOptions());
        if (!await ctx.Tenants.AnyAsync(t => t.Id == tenantId))
            ctx.Tenants.Add(Tenant.CreateWithId(tenantId, ApiTestFactory.TenantName));
        await ctx.SaveChangesAsync();

        // wfA: LogAudit(1) -> Delay(2) -> NotificationSimulation(3).
        var workflowA = Workflow.Create(tenantId, WfAName, "observability workflow A");
        var versionA = WorkflowVersion.Create(workflowA.Id, 1);
        versionA.AddStep(1, nameof(StepType.LogAudit), "{}", "audit");
        versionA.AddStep(2, nameof(StepType.Delay), "{\"seconds\":1}", "wait");
        versionA.AddStep(3, nameof(StepType.NotificationSimulation), "{\"channel\":\"email\"}", "notify");
        versionA.Publish();

        // wfB: HttpWebhook(1) against an unreachable port.
        var workflowB = Workflow.Create(tenantId, WfBName, "observability workflow B");
        var versionB = WorkflowVersion.Create(workflowB.Id, 1);
        versionB.AddStep(1, nameof(StepType.HttpWebhook), "{\"url\":\"http://127.0.0.1:59999\"}", "hook");
        versionB.Publish();

        ctx.Workflows.AddRange(workflowA, workflowB);
        ctx.WorkflowVersions.AddRange(versionA, versionB);
        await ctx.SaveChangesAsync();

        var wfAId = workflowA.Id;
        var wfBId = workflowB.Id;
        var versionAId = versionA.Id;
        var versionBId = versionB.Id;
        var stepA1 = versionA.Steps.Single(s => s.StepNumber == 1);
        var stepA2 = versionA.Steps.Single(s => s.StepNumber == 2);
        var stepA3 = versionA.Steps.Single(s => s.StepNumber == 3);
        var stepB1 = versionB.Steps.Single(s => s.StepNumber == 1);

        var eventA = Event.Create(tenantId, $"obs-a-{Guid.NewGuid():N}", "order.created", seedUtc, "{}");
        var eventB = Event.Create(tenantId, $"obs-b-{Guid.NewGuid():N}", "order.created", seedUtc, "{}");
        var eventC = Event.Create(tenantId, $"obs-c-{Guid.NewGuid():N}", "order.created", seedUtc, "{}");
        ctx.Events.AddRange(eventA, eventB, eventC);
        await ctx.SaveChangesAsync();

        // Execution 1: Succeeded, wfA, StartedAt = now - 10min, all 3 steps completed.
        var exec1 = WorkflowExecution.Create(wfAId, versionAId, eventA.Id, tenantId);
        exec1.Start();
        ctx.Entry(exec1).Property(x => x.StartedAt).CurrentValue = seedUtc.AddMinutes(-10);
        ctx.WorkflowExecutions.Add(exec1);

        var se1 = WorkflowStepExecution.Create(exec1.Id, stepA1.Id, 1);
        se1.Start();
        se1.Succeed("logged");
        ctx.Entry(se1).Property(x => x.StartedAt).CurrentValue = seedUtc.AddMinutes(-10);
        ctx.Entry(se1).Property(x => x.CompletedAt).CurrentValue = seedUtc.AddMinutes(-10).AddMilliseconds(500);

        var se2 = WorkflowStepExecution.Create(exec1.Id, stepA2.Id, 2);
        se2.Start();
        se2.Succeed("waited");
        ctx.Entry(se2).Property(x => x.StartedAt).CurrentValue = seedUtc.AddMinutes(-10).AddMilliseconds(500);
        ctx.Entry(se2).Property(x => x.CompletedAt).CurrentValue = seedUtc.AddMinutes(-10).AddMilliseconds(1500);

        var se3 = WorkflowStepExecution.Create(exec1.Id, stepA3.Id, 3);
        se3.Start();
        se3.Succeed("notified");
        ctx.Entry(se3).Property(x => x.StartedAt).CurrentValue = seedUtc.AddMinutes(-10).AddMilliseconds(1500);
        ctx.Entry(se3).Property(x => x.CompletedAt).CurrentValue = seedUtc.AddMinutes(-10).AddMilliseconds(1700);

        ctx.WorkflowStepExecutions.AddRange(se1, se2, se3);
        ctx.Entry(exec1).Property(x => x.CompletedAt).CurrentValue = seedUtc.AddMinutes(-10).AddSeconds(2);
        ctx.Entry(exec1).Property(x => x.Status).CurrentValue = WorkflowExecutionStatus.Succeeded;
        await ctx.SaveChangesAsync();

        // Execution 2: Running, wfA, StartedAt = now - 5min, step 1 done.
        var exec2 = WorkflowExecution.Create(wfAId, versionAId, eventB.Id, tenantId);
        exec2.Start();
        ctx.Entry(exec2).Property(x => x.StartedAt).CurrentValue = seedUtc.AddMinutes(-5);
        ctx.WorkflowExecutions.Add(exec2);

        var se4 = WorkflowStepExecution.Create(exec2.Id, stepA1.Id, 1);
        se4.Start();
        se4.Succeed("logged");
        ctx.Entry(se4).Property(x => x.StartedAt).CurrentValue = seedUtc.AddMinutes(-5);
        ctx.Entry(se4).Property(x => x.CompletedAt).CurrentValue = seedUtc.AddMinutes(-5).AddMilliseconds(300);
        ctx.WorkflowStepExecutions.Add(se4);
        await ctx.SaveChangesAsync();

        // Execution 3: Failed, wfB, StartedAt = now - 2h, one step retried (attempt 2) then failed.
        var exec3 = WorkflowExecution.Create(wfBId, versionBId, eventC.Id, tenantId);
        exec3.Start();
        ctx.Entry(exec3).Property(x => x.StartedAt).CurrentValue = seedUtc.AddHours(-2);
        ctx.WorkflowExecutions.Add(exec3);

        var se5 = WorkflowStepExecution.Create(exec3.Id, stepB1.Id, 1, maxAttempts: 2);
        se5.Start();
        se5.Fail("connection refused");
        ctx.Entry(se5).Property(x => x.AttemptNumber).CurrentValue = 2;
        ctx.Entry(se5).Property(x => x.StartedAt).CurrentValue = seedUtc.AddHours(-2);
        ctx.Entry(se5).Property(x => x.CompletedAt).CurrentValue = seedUtc.AddHours(-2).AddMilliseconds(250);
        ctx.WorkflowStepExecutions.Add(se5);

        ctx.Entry(exec3).Property(x => x.Status).CurrentValue = WorkflowExecutionStatus.Failed;
        ctx.Entry(exec3).Property(x => x.RetryCount).CurrentValue = 1;
        ctx.Entry(exec3).Property(x => x.ErrorMessage).CurrentValue = "execution boom";
        ctx.Entry(exec3).Property(x => x.CompletedAt).CurrentValue = seedUtc.AddHours(-2).AddSeconds(1);
        await ctx.SaveChangesAsync();

        // Execution 4: Retrying, wfA, StartedAt = now - 1min, no completed steps.
        var exec4 = WorkflowExecution.Create(wfAId, versionAId, eventA.Id, tenantId);
        exec4.Start();
        ctx.Entry(exec4).Property(x => x.StartedAt).CurrentValue = seedUtc.AddMinutes(-1);
        ctx.Entry(exec4).Property(x => x.Status).CurrentValue = WorkflowExecutionStatus.Retrying;
        ctx.WorkflowExecutions.Add(exec4);
        await ctx.SaveChangesAsync();

        _wfAId = wfAId;
        _wfBId = wfBId;
        _seedUtc = seedUtc;
        _seeded = true;
    }

    private async Task SeedTenantBAsync()
    {
        using var ctx = new SignalForgeDbContext(DbOptions());
        if (!await ctx.ApiKeys.AnyAsync(ak => ak.KeyPrefix == KeyB.Substring(0, 8)))
        {
            ctx.Tenants.Add(Tenant.CreateWithId(TenantB, "itest-tenant-b"));
            ctx.ApiKeys.Add(ApiKey.Create(TenantB, "itest-key-b", KeyB));
            ctx.TenantWebhookSigningSettings.Add(
                TenantWebhookSigningSetting.Create(TenantB, "sfTestK2-tenant-b-signing-secret"));
            await ctx.SaveChangesAsync();
        }
    }
}