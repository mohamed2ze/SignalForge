using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.UnitTests.Services;

public class EventIngestionServiceTests
{
    private static SignalForgeDbContext CreateDb()
        => new(new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (EventIngestionService Service, SignalForgeDbContext Db) Create()
    {
        var db = CreateDb();
        return (new EventIngestionService(db, new RecordingPublisher()), db);
    }

    [Fact]
    public async Task Ingest_same_external_event_id_twice_is_idempotent()
    {
        var (service, db) = Create();
        var tenantId = Guid.NewGuid();

        var first = await service.IngestEventAsync(tenantId, "ext-1", "order.created", DateTime.UtcNow, "{}");
        var second = await service.IngestEventAsync(tenantId, "ext-1", "order.created", DateTime.UtcNow, "{}");

        Assert.True(first.IsNewEvent);
        Assert.False(second.IsNewEvent);
        Assert.Equal(first.Event.Id, second.Event.Id);
        Assert.Single(await db.Events.ToListAsync());
    }

    [Fact]
    public async Task Same_external_event_id_in_another_tenant_creates_a_new_event()
    {
        var (service, db) = Create();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var a = await service.IngestEventAsync(tenantA, "ext-1", "order.created", DateTime.UtcNow, "{}");
        var b = await service.IngestEventAsync(tenantB, "ext-1", "order.created", DateTime.UtcNow, "{}");

        Assert.True(a.IsNewEvent);
        Assert.True(b.IsNewEvent);
        Assert.NotEqual(a.Event.Id, b.Event.Id);
        Assert.Equal(a.Event.TenantId, tenantA);
        Assert.Equal(b.Event.TenantId, tenantB);
        Assert.Equal(2, await db.Events.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_external_event_id_is_rejected(string externalEventId)
    {
        var (service, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestEventAsync(Guid.NewGuid(), externalEventId, "order.created", DateTime.UtcNow, "{}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_payload_is_rejected(string payload)
    {
        var (service, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestEventAsync(Guid.NewGuid(), "ext-1", "order.created", DateTime.UtcNow, payload));
    }

    [Fact]
    public async Task Publish_delegates_to_outbox_with_tenant_id()
    {
        var publisher = new RecordingPublisher();
        var db = CreateDb();
        var service = new EventIngestionService(db, publisher);

        await service.PublishAsync(Guid.NewGuid(), "order.created", """{"a":1}""");

        var (tenantId, type, payload) = Assert.Single(publisher.Published);
        Assert.Equal("order.created", type);
        Assert.Equal("""{"a":1}""", payload);
        Assert.NotEqual(Guid.Empty, tenantId);
    }

    private sealed class RecordingPublisher : IOutboxPublisher
    {
        public List<(Guid TenantId, string Type, string Payload)> Published { get; } = new();

        public Task PublishAsync(
            Guid tenantId, string type, string payload, CancellationToken cancellationToken = default)
        {
            Published.Add((tenantId, type, payload));
            return Task.CompletedTask;
        }
    }
}