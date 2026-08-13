namespace SignalForge.Application.Broker;

/// <summary>
/// Transport abstraction for publishing messages (ports/adapters).
/// Implementations are entirely transport-specific: in-memory for local dev/tests,
/// with Kafka / RabbitMQ / HTTP adapters conceivable later. Kept deliberately
/// CloudEvents-free — envelope concerns belong to a later stage.
/// </summary>
public interface IMessageBroker
{
    /// <summary>
    /// Publishes a message to the broker.
    /// </summary>
    /// <param name="type">The message type</param>
    /// <param name="payload">The message payload</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns><see langword="true"/> if the message was accepted; <see langword="false"/>
    /// if the broker rejected it without throwing (callers translate that into a failure).</returns>
    Task<bool> PublishAsync(string type, string payload, CancellationToken cancellationToken = default);
}