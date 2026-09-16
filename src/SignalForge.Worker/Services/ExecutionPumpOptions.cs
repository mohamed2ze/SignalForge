namespace SignalForge.Worker.Services;

public class ExecutionPumpOptions
{
    public int BatchSize { get; set; } = 10;

    /// <summary>
    /// How many times <see cref="BatchSize"/> the pump scans before applying per-tenant fairness.
    /// The batch is then filled round-robin (one candidate per tenant per round, oldest first) so
    /// a flood from one tenant cannot starve quieter tenants' executions.
    /// </summary>
    public int ScanMultiplier { get; set; } = 5;

    public double PollIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// Seconds a claimed execution stays invisible to other workers. If the claiming worker
    /// crashes mid-advance, the lease expires after this window and another worker reclaims the
    /// execution, so progress resumes after a restart without a manual reset.
    /// </summary>
    public double ClaimLeaseSeconds { get; set; } = 300;
}