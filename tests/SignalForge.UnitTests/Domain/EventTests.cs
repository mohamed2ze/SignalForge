using SignalForge.Domain.Models;

namespace SignalForge.UnitTests.Domain;

public class EventTests
{
    [Fact]
    public void Create_sets_defaults()
    {
        var occurredAt = new DateTime(2026, 1, 1, 10, 30, 0, DateTimeKind.Utc);
        var eventEntity = Event.Create(Guid.NewGuid(), "ext-1", "order.created", occurredAt, "{}");

        Assert.NotEqual(Guid.Empty, eventEntity.Id);
        Assert.Equal("ext-1", eventEntity.ExternalEventId);
        Assert.Equal("order.created", eventEntity.EventType);
        Assert.Equal(occurredAt, eventEntity.OccurredAt);
        Assert.Equal("{}", eventEntity.Payload);
        Assert.NotEqual(default, eventEntity.ReceivedAt);
        Assert.False(eventEntity.IsProcessed);
        Assert.Null(eventEntity.ProcessedAt);
    }

    [Fact]
    public void Create_trims_external_id_and_type()
    {
        var eventEntity = Event.Create(
            Guid.NewGuid(), "  ext-1  ", "  order.created  ", DateTime.UtcNow, "{}");

        Assert.Equal("ext-1", eventEntity.ExternalEventId);
        Assert.Equal("order.created", eventEntity.EventType);
    }

    [Fact]
    public void Create_normalizes_occurred_at_to_utc()
    {
        var eventEntity = Event.Create(
            Guid.NewGuid(), "ext-1", "order.created",
            new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc), "{}");

        Assert.Equal(DateTimeKind.Utc, eventEntity.OccurredAt.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_external_id(string? externalId)
    {
        Assert.Throws<ArgumentException>(() =>
            Event.Create(Guid.NewGuid(), externalId!, "order.created", DateTime.UtcNow, "{}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_event_type(string? eventType)
    {
        Assert.Throws<ArgumentException>(() =>
            Event.Create(Guid.NewGuid(), "ext-1", eventType!, DateTime.UtcNow, "{}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_payload(string? payload)
    {
        Assert.Throws<ArgumentException>(() =>
            Event.Create(Guid.NewGuid(), "ext-1", "order.created", DateTime.UtcNow, payload!));
    }

    [Fact]
    public void Create_rejects_payload_above_the_maximum_length()
    {
        var oversized = new string('a', Event.PayloadMaxLength + 1);

        Assert.Throws<ArgumentException>(() =>
            Event.Create(Guid.NewGuid(), "ext-1", "order.created", DateTime.UtcNow, oversized));
    }

    [Fact]
    public void Create_accepts_payload_at_the_maximum_length()
    {
        var atLimit = new string('a', Event.PayloadMaxLength);

        var eventEntity = Event.Create(Guid.NewGuid(), "ext-1", "order.created", DateTime.UtcNow, atLimit);

        Assert.Equal(Event.PayloadMaxLength, eventEntity.Payload.Length);
    }

    [Fact]
    public void MarkAsProcessed_sets_state()
    {
        var eventEntity = Event.Create(Guid.NewGuid(), "ext-1", "order.created", DateTime.UtcNow, "{}");

        eventEntity.MarkAsProcessed();

        Assert.True(eventEntity.IsProcessed);
        Assert.NotNull(eventEntity.ProcessedAt);
    }

    [Fact]
    public void IsSameEvent_matches_tenant_and_external_id()
    {
        var tenantId = Guid.NewGuid();
        var eventEntity = Event.Create(tenantId, "ext-1", "order.created", DateTime.UtcNow, "{}");

        Assert.True(eventEntity.IsSameEvent(tenantId, "ext-1"));
        Assert.False(eventEntity.IsSameEvent(Guid.NewGuid(), "ext-1"));
        Assert.False(eventEntity.IsSameEvent(tenantId, "ext-2"));
    }
}