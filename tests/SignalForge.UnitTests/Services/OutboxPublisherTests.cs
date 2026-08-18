using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Services;
using SignalForge.Infrastructure.Data;

namespace SignalForge.UnitTests.Services;

public class OutboxPublisherTests
{
    [Fact]
    public async Task Publish_writes_an_unprocessed_message_with_the_tenant_id()
    {
        var options = new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new SignalForgeDbContext(options);
        var tenantId = Guid.NewGuid();

        var publisher = new OutboxPublisher(db);
        await publisher.PublishAsync(tenantId, "OrderCreated", """{"id":1}""");

        var message = await db.OutboxMessages.SingleAsync();

        Assert.Equal(tenantId, message.TenantId);
        Assert.Equal("OrderCreated", message.Type);
        Assert.Equal("""{"id":1}""", message.Payload);
        Assert.False(message.IsProcessed);
        Assert.Equal(0, message.AttemptCount);
    }
}