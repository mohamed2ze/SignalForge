namespace SignalForge.Application.Broker;

/// <summary>
/// Transport abstraction for publishing messages (ports/adapters).
/// Implementations are entirely transport-specific: in-memory for local dev/tests,
/// with Kafka / RabbitMQ / HTTP adapters conceivable later. Kept deliberately
/// CloudEvents-free — envelope concerns belong to a later stage.
/// </summary>
public interface IMessageBroker
{
    Task<bool> PublishAsync(string type, string payload, CancellationToken cancellationToken = default);
}