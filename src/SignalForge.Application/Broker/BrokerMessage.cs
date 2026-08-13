namespace SignalForge.Application.Broker;

/// <summary>
/// A message as held in a broker store. Used for readback/audit during local dev,
/// verification, and tests (an in-memory broker has no external process to inspect).
/// </summary>
public sealed record BrokerMessage(string Id, string Type, string Payload, DateTime PublishedAt);