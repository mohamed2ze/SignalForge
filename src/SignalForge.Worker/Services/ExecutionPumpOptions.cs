namespace SignalForge.Worker.Services;

/// <summary>
/// Options for the workflow execution pump (config section: "ExecutionPump").
/// </summary>
public class ExecutionPumpOptions
{
    /// <summary>Maximum number of runnable executions advanced per poll cycle.</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>Seconds between poll cycles when there is nothing left to advance.</summary>
    public double PollIntervalSeconds { get; set; } = 2;
}