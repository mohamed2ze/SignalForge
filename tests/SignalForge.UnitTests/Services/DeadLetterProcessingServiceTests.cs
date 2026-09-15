using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.UnitTests.Services;

public class DeadLetterProcessingServiceTests
{
    private static SignalForgeDbContext CreateDb()
        => new(new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DeadLetterMessage CreateDeadLetter(Guid tenantId, string type, string error)
        => DeadLetterMessage.CreateFromOutboxMessage(
            OutboxMessage.Create(tenantId, type, "{}"), error);

    private static async Task<(DeadLetterProcessingService Service, List<DeadLetterMessage> A, List<DeadLetterMessage> B)>
        SeedAsync()
    {
        var db = CreateDb();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var a = new List<DeadLetterMessage>
        {
            CreateDeadLetter(tenantA, "Test/Fail", "a-1"),
            CreateDeadLetter(tenantA, "Test/Fail", "a-2"),
        };
        var b = new List<DeadLetterMessage>
        {
            CreateDeadLetter(tenantB, "Test/Fail", "b-1"),
        };

        db.DeadLetterMessages.AddRange(a);
        db.DeadLetterMessages.AddRange(b);
        await db.SaveChangesAsync();

        return (new DeadLetterProcessingService(db, new NeverUniqueViolationDetector()), a, b);
    }

    [Fact]
    public async Task List_is_scoped_to_the_requesting_tenant()
    {
        var (service, a, b) = await SeedAsync();

        var tenantAList = await service.GetDeadLettersAsync(a[0].TenantId);
        var tenantBList = await service.GetDeadLettersAsync(b[0].TenantId);

        Assert.Equal(2, tenantAList.Count);
        Assert.Single(tenantBList);
        Assert.All(tenantAList, dl => Assert.Equal(a[0].TenantId, dl.TenantId));
        Assert.DoesNotContain(tenantAList, dl => dl.Id == b[0].Id);
    }

    [Fact]
    public async Task Get_by_id_returns_null_for_other_tenants()
    {
        var (service, a, b) = await SeedAsync();

        Assert.NotNull(await service.GetDeadLetterByIdAsync(a[0].Id, a[0].TenantId));
        Assert.Null(await service.GetDeadLetterByIdAsync(a[0].Id, b[0].TenantId));
        Assert.Null(await service.GetDeadLetterByIdAsync(a[0].Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task Mark_as_processed_is_tenant_scoped_and_idempotent()
    {
        var (service, a, b) = await SeedAsync();
        var tenantA = a[0].TenantId;

        Assert.True(await service.MarkAsProcessedAsync(a[0].Id, tenantA));
        Assert.False(await service.MarkAsProcessedAsync(a[0].Id, b[0].TenantId));
        Assert.True(await service.MarkAsProcessedAsync(a[0].Id, tenantA));

        var counts = await service.GetDeadLetterCountsAsync(tenantA);
        Assert.Equal(2, counts.Total);
        Assert.Equal(1, counts.Unprocessed);
        Assert.Equal(1, counts.Processed);
    }

    [Fact]
    public async Task Counts_reflect_totals_and_processing_state()
    {
        var (service, a, _) = await SeedAsync();
        await service.MarkAsProcessedAsync(a[0].Id, a[0].TenantId);

        var counts = await service.GetDeadLetterCountsAsync(a[0].TenantId);

        Assert.Equal(2, counts.Total);
        Assert.Equal(1, counts.Processed);
        Assert.Equal(1, counts.Unprocessed);
    }

    [Fact]
    public async Task Only_unprocessed_filter_applies()
    {
        var (service, a, _) = await SeedAsync();
        await service.MarkAsProcessedAsync(a[0].Id, a[0].TenantId);

        var unprocessed = await service.GetDeadLettersAsync(a[0].TenantId, onlyUnprocessed: true);

        Assert.Single(unprocessed);
        Assert.Equal(a[1].Id, unprocessed[0].Id);
    }
}