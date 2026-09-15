using SignalForge.Application.Data;

namespace SignalForge.UnitTests;

/// <summary>
/// Detector double that never reports a unique violation. The EF Core in-memory provider used by
/// unit tests has no relational unique-constraint enforcement, so services can never hit the
/// idempotency-race path through it; the integration suite covers the real SQL Server detection.
/// </summary>
public sealed class NeverUniqueViolationDetector : IUniqueViolationDetector
{
    public bool IsUniqueViolation(Exception exception) => false;
}