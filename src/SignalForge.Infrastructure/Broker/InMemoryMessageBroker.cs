using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using SignalForge.Application.Broker;

namespace SignalForge.Infrastructure.Broker
{
    /// <summary>
    /// In-memory <see cref="IMessageBroker"/> + <see cref="IBrokerAudit"/> for local development and
    /// testing. Submission order is preserved; readback happens through <see cref="IBrokerAudit"/>.
    /// This is a development tool, not a production transport — swap the DI registration for a real
    /// adapter (Kafka / RabbitMQ / HTTP) when one exists.
    /// </summary>
    public sealed class InMemoryMessageBroker : IMessageBroker, IBrokerAudit
    {
        private readonly ConcurrentQueue<BrokerMessage> _messages = new();

        public Task<bool> PublishAsync(string type, string payload, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(type))
                return Task.FromResult(false);

            _messages.Enqueue(new BrokerMessage(
                Guid.NewGuid().ToString("N"),
                type,
                payload,
                DateTime.UtcNow));

            return Task.FromResult(true);
        }

        public IReadOnlyList<BrokerMessage> GetAll() => _messages.ToArray();

        public Task<IReadOnlyList<BrokerMessage>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(GetAll());
    }
}