namespace SignalForge.Application.Broker;

/// <summary>
/// Readback/audit view over a broker store. Lets verification and tests observe messages
/// without coupling to a concrete transport (e.g. no external broker process in dev).
/// </summary>
public interface IBrokerAudit
{
    IReadOnlyList<BrokerMessage> GetAll();

    Task<IReadOnlyList<BrokerMessage>> GetAllAsync(CancellationToken cancellationToken = default);
}