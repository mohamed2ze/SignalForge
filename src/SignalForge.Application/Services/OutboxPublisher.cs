using System.Threading;
using System.Threading.Tasks;
using SignalForge.Application.Data;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services
{
    public class OutboxPublisher : IOutboxPublisher
    {
        private readonly ISignalForgeDbContext _dbContext;

        public OutboxPublisher(ISignalForgeDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public Task PublishAsync(Guid tenantId, string type, string payload, CancellationToken cancellationToken = default)
        {
            // Additive within the caller's unit of work: the message is committed (or rolled
            // back) alongside the caller's own SaveChangesAsync. For emissions this keeps the
            // outbox write atomic with the step's success transition — a crash before that save
            // leaves neither an outbox message nor a succeeded step, and the at-least-once
            // pump recovery re-runs the step and re-emits instead of dangling a message.
            var outboxMessage = OutboxMessage.Create(tenantId, type, payload);

            _dbContext.OutboxMessages.Add(outboxMessage);
            return Task.CompletedTask;
        }
    }
}