namespace SignalForge.Worker.Services;

/// <summary>
/// Options for the workflow execution pump (config section: "ExecutionPump").
/// </summary>
public class ExecutionPumpOptions
{
    /// <summary>Maximum number of runnable executions advanced per poll cycle.</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>
    /// How many times <see cref="BatchSize"/> the pump scans before applying per-tenant fairness.
    /// The batch is then filled round-robin (one candidate per tenant per round, oldest first) so
    /// a flood from one tenant cannot starve quieter tenants' executions.
    /// </summary>
    public int ScanMultiplier { get; set; } = 5;

    /// <summary>Seconds between poll cycles when there is nothing left to advance.</summary>
    public double PollIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// Seconds a claimed execution stays invisible to other workers. If the claiming worker
    /// crashes mid-advance, the lease expires after this window and another worker reclaims the
    /// execution, so progress resumes after a restart without a manual reset.
    /// </summary>
    public double ClaimLeaseSeconds { get; set; } = 300;
}