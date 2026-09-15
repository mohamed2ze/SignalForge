namespace SignalForge.Worker.Services;

/// <summary>
/// Host-level settings for the worker process (config section: "Worker").
/// </summary>
public class HostingOptions
{
    /// <summary>
    /// How long a poll/pump cycle already in flight may keep running after the host begins
    /// shutting down. New cycles stop at shutdown; an in-flight cycle gets this window to finish
    /// its current work before the drain token hard-cancels it, so a graceful stop never
    /// interrupts a send/advance mid-request. Also a safety net for truly stuck cycles.
    /// </summary>
    public double GracefulShutdownTimeoutSeconds { get; set; } = 30;
}