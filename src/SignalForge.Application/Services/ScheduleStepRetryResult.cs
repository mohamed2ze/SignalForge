using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

public enum StepRetryStatus
{
    Scheduled,

    NotEligible,

    NotFound
}

public sealed record ScheduleStepRetryResult(StepRetryStatus Status, WorkflowStepExecution? StepExecution);