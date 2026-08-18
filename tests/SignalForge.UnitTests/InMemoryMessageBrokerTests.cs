using SignalForge.Infrastructure.Broker;

namespace SignalForge.UnitTests;

public class InMemoryMessageBrokerTests
{
    [Fact]
    public async Task Publish_Stores_Message_Readable_Via_Audit()
    {
        var broker = new InMemoryMessageBroker();

        bool accepted = await broker.PublishAsync("OrderCreated", "{\"id\":1}");

        Assert.True(accepted);
        IReadOnlyList<SignalForge.Application.Broker.BrokerMessage> all = broker.GetAll();
        Assert.Single(all);
        Assert.Equal("OrderCreated", all[0].Type);
        Assert.Equal("{\"id\":1}", all[0].Payload);
        Assert.False(string.IsNullOrWhiteSpace(all[0].Id));
        Assert.NotEqual(default, all[0].PublishedAt);
    }

    [Fact]
    public async Task GetAllAsync_Matches_GetAll()
    {
        var broker = new InMemoryMessageBroker();
        await broker.PublishAsync("A", "1");
        await broker.PublishAsync("B", "2");

        IReadOnlyList<SignalForge.Application.Broker.BrokerMessage> all = await broker.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("A", all[0].Type);
        Assert.Equal("B", all[1].Type);
    }

    [Fact]
    public async Task Publish_Preserves_Order()
    {
        var broker = new InMemoryMessageBroker();

        for (int i = 1; i <= 5; i++)
            await broker.PublishAsync($"Type{i}", i.ToString());

        var all = broker.GetAll();
        Assert.Equal(Enumerable.Range(1, 5).Select(i => $"Type{i}"), all.Select(m => m.Type));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Invalid_Type_Rejected_And_Not_Stored(string? type)
    {
        var broker = new InMemoryMessageBroker();

        bool accepted = await broker.PublishAsync(type!, "payload");

        Assert.False(accepted);
        Assert.Empty(broker.GetAll());
    }
}