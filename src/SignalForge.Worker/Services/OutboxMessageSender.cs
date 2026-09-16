using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SignalForge.Application.Broker;

namespace SignalForge.Worker.Services
{
    public class OutboxMessageSender : IOutboxMessageSender
    {
        private readonly IMessageBroker _broker;
        private readonly ILogger<OutboxMessageSender> _logger;

        public OutboxMessageSender(IMessageBroker broker, ILogger<OutboxMessageSender> logger)
        {
            _broker = broker;
            _logger = logger;
        }

        public async Task SendAsync(string type, string payload, CancellationToken cancellationToken = default)
        {
            bool published = await _broker.PublishAsync(type, payload, cancellationToken);
            if (!published)
            {
                throw new InvalidOperationException($"Broker rejected message of type '{type}'");
            }

            _logger.LogInformation("Published outbox message: Type={Type}, PayloadLength={PayloadLength}",
                type, payload.Length);
        }
    }
}