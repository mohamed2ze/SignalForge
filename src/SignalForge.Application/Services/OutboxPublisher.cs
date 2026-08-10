using System.Threading;
using System.Threading.Tasks;
using SignalForge.Application.Data;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services
{
    /// <summary>
    /// Implementation of the outbox publisher that writes messages to the outbox table.
    /// </summary>
    public class OutboxPublisher : IOutboxPublisher
    {
        private readonly ISignalForgeDbContext _dbContext;

        public OutboxPublisher(ISignalForgeDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <inheritdoc />
        public async Task PublishAsync(Guid tenantId, string type, string payload, CancellationToken cancellationToken = default)
        {
            var outboxMessage = OutboxMessage.Create(tenantId, type, payload);

            _dbContext.OutboxMessages.Add(outboxMessage);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}