using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

public enum StepFailureResolution
{
    /// <summary>The step has attempts remaining and was rescheduled with exponential backoff.</summary>
    Retrying,

    DeadLettered
}