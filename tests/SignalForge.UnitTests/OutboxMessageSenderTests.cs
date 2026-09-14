using Microsoft.Extensions.Logging.Abstractions;
using SignalForge.Application.Broker;
using SignalForge.Infrastructure.Broker;
using SignalForge.Worker.Services;

namespace SignalForge.UnitTests;

public class OutboxMessageSenderTests
{
    private sealed class FakeBroker : IMessageBroker
    {
        public bool Result { get; init; }
        public int CallCount { get; private set; }
        public string? LastType { get; private set; }

        public Task<bool> PublishAsync(string type, string payload, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastType = type;
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task TestFail_Type_Is_No_Longer_Rejected_By_The_Sender()
    {
        var broker = new FakeBroker { Result = true };
        var sender = new OutboxMessageSender(broker, NullLogger<OutboxMessageSender>.Instance);

        await sender.SendAsync("Test/Fail", "{}");

        Assert.Equal(1, broker.CallCount);
        Assert.Equal("Test/Fail", broker.LastType);
    }

    [Fact]
    public async Task Broker_Rejects_Message_Throws()
    {
        var broker = new FakeBroker { Result = false };
        var sender = new OutboxMessageSender(broker, NullLogger<OutboxMessageSender>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsync("OrderCreated", "{}"));

        Assert.Contains("rejected", ex.Message);
        Assert.Equal(1, broker.CallCount);
    }

    [Fact]
    public async Task Broker_Accepts_Message_Succeeds()
    {
        var broker = new FakeBroker { Result = true };
        var sender = new OutboxMessageSender(broker, NullLogger<OutboxMessageSender>.Instance);

        await sender.SendAsync("OrderCreated", "{\"id\":1}");

        Assert.Equal(1, broker.CallCount);
        Assert.Equal("OrderCreated", broker.LastType);
    }

    [Fact]
    public async Task RealBroker_EndToEnd_Stores_The_Message()
    {
        var broker = new InMemoryMessageBroker();
        var sender = new OutboxMessageSender(broker, NullLogger<OutboxMessageSender>.Instance);

        await sender.SendAsync("OrderCreated", "{\"id\":1}");

        Assert.Single(broker.GetAll());
        Assert.Equal("OrderCreated", broker.GetAll()[0].Type);
    }
}