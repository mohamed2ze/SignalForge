using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalForge.Application.Data;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Broker;
using SignalForge.Infrastructure.Data;
using SignalForge.Worker.Services;

namespace SignalForge.IntegrationTests;

/// <summary>
/// Level 5 (Decision #25) behavioural coverage for dead-letter filtering, paging, and replay:
/// the paged/filtered list envelope, the single-in-flight replay rule, and the provenance chain
/// (replay → outbox requeue → terminal re-failure → NEW dead letter linked back via
/// ReplayedFromDeadLetterId). Uses its own dedicated tenant so the assertions are deterministic
/// regardless of what earlier tests left in the shared container DB.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class DeadLetterReplayTests : ApiTestBase
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly string KeyA = $"aA{Guid.NewGuid():N}dlTenantA";
    private static readonly string KeyB = $"bB{Guid.NewGuid():N}dlTenantB";

    public DeadLetterReplayTests(MsSqlContainerFixture database)
        : base(database)
    {
    }

    [Fact]
    public async Task List_Applies_Filters_And_Deterministic_Paging()
    {
        using var ctx = new SignalForgeDbContext(DbOptions());
        await SeedTenantsAsync();

        var seedUtc = DateTime.UtcNow;
        var (workflowId, stepDeadLetter) = await SeedStepFailureDeadLetterAsync(ctx, TenantA, "dl-step-wf", seedUtc.AddMinutes(-10));
        var (out1, out2) = await SeedOutboxDeadLettersAsync(ctx, seedUtc);
        var client = Factory.CreateClient(KeyA);

        try
        {
            // All 3 dead letters, newest first (out2 = -1min, out1 = -5min, step = -10min).
            var all = await GetPagedAsync(client, $"/{ApiDeadLetterRoute}");
            Assert.Equal(3, all.TotalCount);
            Assert.Equal(new[] { out2, out1, stepDeadLetter }, all.Items.Select(d => d["id"]!.AsGuid()));
            Assert.Equal(1, all.Page);
            Assert.Equal(20, all.PageSize);

            // Page 1 size 1: only the newest is returned.
            var page1 = await GetPagedAsync(client, $"/{ApiDeadLetterRoute}?page=1&pageSize=1");
            Assert.Equal(3, page1.TotalCount);
            Assert.Single(page1.Items);
            Assert.Equal(out2, page1.Items[0]["id"]!.AsGuid());

            // WorkflowId filter (step-provenance join): only the step-originated dead letter.
            var byWf = await GetPagedAsync(client, $"/{ApiDeadLetterRoute}?workflowId={workflowId}");
            Assert.Equal(1, byWf.TotalCount);
            Assert.Equal(stepDeadLetter, byWf.Items[0]["id"]!.AsGuid());

            // Cause substring: only outbox-originated ("simulated failure").
            var byCause = await GetPagedAsync(client, $"/{ApiDeadLetterRoute}?cause={Uri.EscapeDataString("simulated")}");
            Assert.Equal(2, byCause.TotalCount);
            Assert.Contains(out1, byCause.Items.Select(d => d["id"]!.AsGuid()));

            // onlyUnprocessed excludes the processed one.
            var unprocessed = await GetPagedAsync(client, $"/{ApiDeadLetterRoute}?onlyUnprocessed=true");
            Assert.Equal(2, unprocessed.TotalCount);
            Assert.DoesNotContain(out1, unprocessed.Items.Select(d => d["id"]!.AsGuid()));

            // Time window: from = -2min => only out2 (CreatedAt -1min).
            var fromWindow = await GetPagedAsync(client,
                $"/{ApiDeadLetterRoute}?from={Uri.EscapeDataString(seedUtc.AddMinutes(-2).ToString("o"))}");
            Assert.Equal(1, fromWindow.TotalCount);
            Assert.Equal(out2, fromWindow.Items[0]["id"]!.AsGuid());

            // Time window: to = older than everything => empty.
            var toWindow = await GetPagedAsync(client,
                $"/{ApiDeadLetterRoute}?to={Uri.EscapeDataString(seedUtc.AddMinutes(-30).ToString("o"))}");
            Assert.Equal(0, toWindow.TotalCount);
            Assert.Empty(toWindow.Items);

            // Gas-bad window values are rejected with 400.
            var badWindow = await client.GetAsync($"/{ApiDeadLetterRoute}?from=not-a-date");
            Assert.Equal(HttpStatusCode.BadRequest, badWindow.StatusCode);

            // Cross-tenant: nothing leaks to tenant B, and B can replay nothing of A's.
            var clientB = Factory.CreateClient(KeyB);
            var bList = await GetPagedAsync(clientB, $"/{ApiDeadLetterRoute}");
            Assert.Equal(0, bList.TotalCount);
            Assert.Empty(bList.Items);
        }
        finally
        {
            await CleanupAsync(TenantA, TenantB);
        }
    }

    [Fact]
    public async Task Replay_Requeues_And_Enforces_Single_InFlight()
    {
        using var ctx = new SignalForgeDbContext(DbOptions());
        await SeedTenantsAsync();
        var (_, deadLetterId) = await SeedOutboxDeadLettersAsync(ctx, DateTime.UtcNow);
        var client = Factory.CreateClient(KeyA);

        try
        {
            // First replay: 200, requeue created + replay recorded.
            var replayed = await client.PostAsync($"/{ApiDeadLetterRoute}/{deadLetterId}/replay", null);
            Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
            var replayBody = JsonNode.Parse(await replayed.Content.ReadAsStringAsync())!.AsObject();
            Assert.Equal(deadLetterId, replayBody["id"]!.AsGuid());
            Assert.Equal(1, (int)replayBody["replayCount"]!);
            Assert.NotNull(replayBody["lastReplayedAt"]);

            using (var verify = new SignalForgeDbContext(DbOptions()))
            {
                var requeued = await verify.OutboxMessages
                    .Where(m => m.ReplaySourceDeadLetterId == deadLetterId)
                    .ToListAsync();
                var single = Assert.Single(requeued);
                Assert.Equal(TenantA, single.TenantId);
                Assert.Equal("Test/Fail", single.Type);
                Assert.Equal("""{"boom":true}""", single.Payload);
                Assert.False(single.IsProcessed);
            }

            // Second replay while the requeue is still in flight: 409, no duplicate requeue.
            var inFlight = await client.PostAsync($"/{ApiDeadLetterRoute}/{deadLetterId}/replay", null);
            Assert.Equal(HttpStatusCode.Conflict, inFlight.StatusCode);

            using (var verify = new SignalForgeDbContext(DbOptions()))
            {
                var requeued = await verify.OutboxMessages
                    .Where(m => m.ReplaySourceDeadLetterId == deadLetterId)
                    .ToListAsync();
                Assert.Single(requeued);
                var dl = await verify.DeadLetterMessages.SingleAsync(d => d.Id == deadLetterId);
                Assert.Equal(1, dl.ReplayCount);
            }

            // Cross-tenant replay of A's dead letter: 404, and B sees no dead letters.
            var clientB = Factory.CreateClient(KeyB);
            var cross = await clientB.PostAsync($"/{ApiDeadLetterRoute}/{deadLetterId}/replay", null);
            Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
            var bList = await GetPagedAsync(clientB, $"/{ApiDeadLetterRoute}");
            Assert.Equal(0, bList.TotalCount);
        }
        finally
        {
            await CleanupAsync(TenantA, TenantB);
        }
    }

    [Fact]
    public async Task Replayed_Message_ReFailing_Ends_As_Linked_DeadLetter()
    {
        using var ctx = new SignalForgeDbContext(DbOptions());
        await SeedTenantsAsync();
        var (_, originalId) = await SeedOutboxDeadLettersAsync(ctx, DateTime.UtcNow);
        var client = Factory.CreateClient(KeyA);

        try
        {
            var replayed = await client.PostAsync($"/{ApiDeadLetterRoute}/{originalId}/replay", null);
            Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);

            // Drive the real outbox processor with the Test/Fail seam (throws before publishing):
            // MaxAttempts = 3 failing cycles => attempt 1, 2, then dead-letter on the 3rd. The
            // per-message retry gate (Decision #26) is collapsed between cycles so the attempts
            // run back-to-back here instead of over the real 2s/4s backoff.
            var processor = CreateProcessor();
            for (var i = 0; i < 3; i++)
            {
                await processor.ProcessBatchAsync(CancellationToken.None);
                if (i < 2)
                    await MakeReplayRetryDueAsync(originalId);
            }

            using (var verify = new SignalForgeDbContext(DbOptions()))
            {
                // The requeue was exhausted into a NEW dead letter linked back to the original.
                var linked = await verify.DeadLetterMessages
                    .Where(d => d.ReplayedFromDeadLetterId == originalId)
                    .ToListAsync();
                var newDl = Assert.Single(linked);
                Assert.Equal("Test/Fail", newDl.OriginalMessageType);
                Assert.Equal("Outbox", newDl.FailedStepType);
                Assert.Equal(3, newDl.FinalAttemptCount);

                // The original is untouched apart from the recorded replay.
                var original = await verify.DeadLetterMessages.SingleAsync(d => d.Id == originalId);
                Assert.Equal(1, original.ReplayCount);
                Assert.Null(original.ReplayedFromDeadLetterId);

                // The outbox table drained the requeue.
                var remaining = await verify.OutboxMessages
                    .Where(m => m.ReplaySourceDeadLetterId == originalId)
                    .ToListAsync();
                Assert.Empty(remaining);

                // Detail reads the full chain: original has no link, the new one points back.
                var detail = await client.GetAsync($"/{ApiDeadLetterRoute}/{newDl.Id}");
                Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
                var detailBody = JsonNode.Parse(await detail.Content.ReadAsStringAsync())!.AsObject();
                Assert.Equal(originalId, detailBody["replayedFromDeadLetterId"]!.AsGuid());
            }

            // In-flight cleared once the requeue was exhausted: replay works again (and bumps
            // ReplayCount to 2 on the original).
            var second = await client.PostAsync($"/{ApiDeadLetterRoute}/{originalId}/replay", null);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            var secondBody = JsonNode.Parse(await second.Content.ReadAsStringAsync())!.AsObject();
            Assert.Equal(2, (int)secondBody["replayCount"]!);
        }
        finally
        {
            await CleanupAsync(TenantA, TenantB);
        }
    }

    // ---------- helpers ----------

    private async Task SeedTenantsAsync()
    {
        using var ctx = new SignalForgeDbContext(DbOptions());
        await EnsureTenantAsync(ctx, TenantA, "itest-dl-tenant-a", KeyA);
        await EnsureTenantAsync(ctx, TenantB, "itest-dl-tenant-b", KeyB);
    }

    private static async Task<(Guid oldProcessed, Guid unprocessed)> SeedOutboxDeadLettersAsync(
        SignalForgeDbContext ctx, DateTime seedUtc)
    {
        // Oldest: already processed (excluded by onlyUnprocessed, still in all).
        var outboxProcessed = OutboxMessage.Create(TenantA, "Test/Fail", """{"boom":true}""");
        var dlProcessed = DeadLetterMessage.CreateFromOutboxMessage(outboxProcessed, "simulated failure");
        dlProcessed.MarkAsProcessed();
        ctx.Entry(dlProcessed).Property(d => d.CreatedAt).CurrentValue = seedUtc.AddMinutes(-5);
        ctx.OutboxMessages.Add(outboxProcessed);
        ctx.DeadLetterMessages.Add(dlProcessed);

        // Newest: unprocessed — the replay target.
        var outboxUnprocessed = OutboxMessage.Create(TenantA, "Test/Fail", """{"boom":true}""");
        var dlUnprocessed = DeadLetterMessage.CreateFromOutboxMessage(outboxUnprocessed, "simulated failure");
        ctx.Entry(dlUnprocessed).Property(d => d.CreatedAt).CurrentValue = seedUtc.AddMinutes(-1);
        ctx.OutboxMessages.Add(outboxUnprocessed);
        ctx.DeadLetterMessages.Add(dlUnprocessed);

        await ctx.SaveChangesAsync();
        return (dlProcessed.Id, dlUnprocessed.Id);
    }

    private OutboxProcessor CreateProcessor()
    {
        var services = new ServiceCollection();
        services.AddScoped<ISignalForgeDbContext>(_ => new SignalForgeDbContext(DbOptions()));
        services.AddScoped<IOutboxMessageSender>(_ =>
            new OutboxMessageSender(
                new InMemoryMessageBroker(),
                NullLogger<OutboxMessageSender>.Instance));

        var provider = services.BuildServiceProvider();
        return new OutboxProcessor(
            provider.GetRequiredService<IServiceScopeFactory>(),
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

    // Collapses the per-message retry gate for a dead letter's in-flight requeue so the next
    // processor cycle re-claims it. Uses the EF value-sink idiom from the observability tests.
    private async Task MakeReplayRetryDueAsync(Guid sourceDeadLetterId)
    {
        using var ctx = new SignalForgeDbContext(DbOptions());
        var requeue = await ctx.OutboxMessages
            .SingleAsync(m => m.ReplaySourceDeadLetterId == sourceDeadLetterId);
        ctx.Entry(requeue).Property(m => m.NextRetryAt).CurrentValue = DateTime.UtcNow.AddSeconds(-1);
        await ctx.SaveChangesAsync();
    }

    private static async Task<(int TotalCount, int PageSize, int Page, List<JsonObject> Items)> GetPagedAsync(
        HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        return (
            (int)body["totalCount"]!,
            (int)body["pageSize"]!,
            (int)body["page"]!,
            body["items"]!.AsArray().Select(n => n!.AsObject()).ToList());
    }
}