using SignalForge.Domain.Models;

namespace SignalForge.UnitTests.Domain;

public class OutboxMessageTests
{
    [Fact]
    public void Create_sets_defaults()
    {
        var message = OutboxMessage.Create(Guid.NewGuid(), "OrderCreated", "{}");

        Assert.NotEqual(Guid.Empty, message.Id);
        Assert.Equal("OrderCreated", message.Type);
        Assert.Equal("{}", message.Payload);
        Assert.Equal(0, message.AttemptCount);
        Assert.False(message.IsProcessed);
        Assert.Null(message.ProcessedAt);
        Assert.Null(message.FailedAt);
        Assert.False(message.IsProcessedSuccessfully());
        Assert.False(message.HasFailed());
    }

    [Fact]
    public void Create_rejects_null_type()
    {
        Assert.Throws<ArgumentNullException>(() => OutboxMessage.Create(Guid.NewGuid(), null!, "{}"));
    }

    [Fact]
    public void Create_rejects_null_payload()
    {
        Assert.Throws<ArgumentNullException>(() => OutboxMessage.Create(Guid.NewGuid(), "type", null!));
    }

    [Fact]
    public void MarkAsProcessed_flips_state()
    {
        var message = OutboxMessage.Create(Guid.NewGuid(), "type", "{}");

        message.MarkAsProcessed();

        Assert.True(message.IsProcessed);
        Assert.NotNull(message.ProcessedAt);
        Assert.True(message.IsProcessedSuccessfully());
    }

    [Fact]
    public void MarkAsFailed_records_error()
    {
        var message = OutboxMessage.Create(Guid.NewGuid(), "type", "{}");

        message.MarkAsFailed("boom");

        Assert.NotNull(message.FailedAt);
        Assert.Equal("boom", message.ErrorMessage);
        Assert.True(message.HasFailed());
        Assert.False(message.IsProcessedSuccessfully());
    }

    [Fact]
    public void MarkAsFailed_rejects_blank_error()
    {
        var message = OutboxMessage.Create(Guid.NewGuid(), "type", "{}");

        Assert.Throws<ArgumentException>(() => message.MarkAsFailed("  "));
    }

    [Fact]
    public void IncrementAttempt_counts_attempts()
    {
        var message = OutboxMessage.Create(Guid.NewGuid(), "type", "{}");

        message.IncrementAttempt();
        message.IncrementAttempt();

        Assert.Equal(2, message.AttemptCount);
    }

    [Fact]
    public void CreateReplay_tags_source_dead_letter()
    {
        var sourceId = Guid.NewGuid();

        var message = OutboxMessage.CreateReplay(Guid.NewGuid(), "OrderCreated", "{}", sourceId);

        Assert.Equal("OrderCreated", message.Type);
        Assert.Equal("{}", message.Payload);
        Assert.Equal(sourceId, message.ReplaySourceDeadLetterId);
        Assert.Equal(0, message.AttemptCount);
        Assert.False(message.IsProcessed);
    }

    [Fact]
    public void CreateReplay_rejects_empty_source_id()
    {
        Assert.Throws<ArgumentException>(() =>
            OutboxMessage.CreateReplay(Guid.NewGuid(), "type", "{}", Guid.Empty));
    }
}