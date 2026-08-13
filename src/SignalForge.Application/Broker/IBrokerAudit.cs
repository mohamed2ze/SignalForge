namespace SignalForge.Application.Broker;

/// <summary>
/// Readback/audit view over a broker store. Lets verification and tests observe messages
/// without coupling to a concrete transport (e.g. no external broker process in dev).
/// </summary>
public interface IBrokerAudit
{
    /// <summary>
    /// Returns all messages currently held by the broker store, in publication order.
    /// </summary>
    IReadOnlyList<BrokerMessage> GetAll();

    /// <summary>
    /// Async overload of <see cref="GetAll"/> for symmetric access patterns.
    /// </summary>
    Task<IReadOnlyList<BrokerMessage>> GetAllAsync(CancellationToken cancellationToken = default);
}