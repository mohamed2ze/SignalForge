using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Broker;
using SignalForge.Application.Data;

namespace SignalForge.Infrastructure.Broker
{
    /// <summary>
    /// Durable <see cref="IMessageBroker"/> + <see cref="IBrokerAudit"/> backed by the shared SQL
    /// database. Messages are appended to the <c>BrokerMessages</c> table in the same transaction
    /// store as everything else, so a worker restart does not lose already-published messages.
    /// Scoped: it owns no shared state and simply reads/writes rows through the request scope's
    /// <see cref="ISignalForgeDbContext"/>.
    /// </summary>
    public sealed class SqlMessageBroker : IMessageBroker, IBrokerAudit
    {
        private readonly ISignalForgeDbContext _db;

        public SqlMessageBroker(ISignalForgeDbContext db)
        {
            _db = db;
        }

        public async Task<bool> PublishAsync(
            string type,
            string payload,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(type))
                return false;

            _db.BrokerMessages.Add(new BrokerMessage(
                Guid.NewGuid().ToString("N"),
                type,
                payload,
                DateTime.UtcNow));

            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        public IReadOnlyList<BrokerMessage> GetAll()
            => _db.BrokerMessages
                .AsNoTracking()
                .OrderBy(m => m.PublishedAt)
                .ToList();

        public async Task<IReadOnlyList<BrokerMessage>> GetAllAsync(
            CancellationToken cancellationToken = default)
            => await _db.BrokerMessages
                .AsNoTracking()
                .OrderBy(m => m.PublishedAt)
                .ToListAsync(cancellationToken);
    }
}