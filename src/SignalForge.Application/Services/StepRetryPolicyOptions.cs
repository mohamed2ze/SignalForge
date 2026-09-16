namespace SignalForge.Application.Services;

/// <summary>
/// Options for the shared step retry/backoff policy (config section: "StepRetry").
/// </summary>
public class StepRetryPolicyOptions
{
    /// <summary>
    /// Upper bound in seconds for the exponential backoff between step attempts.
    /// </summary>
    public double MaxBackoffSeconds { get; set; } = 300;
}