using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

/// <summary>
/// Outcome of a manual step-retry request.
/// </summary>
public enum StepRetryStatus
{
    /// <summary>Retry scheduled: the step moved to <see cref="WorkflowStepExecutionStatus.Retrying"/>
    /// with a new <c>NextRetryAt</c>.</summary>
    Scheduled,

    /// <summary>Step cannot be retried right now: it is in flight (Pending/Running), its retry
    /// window is not due, or it has exhausted its attempts.</summary>
    NotEligible,

    /// <summary>The execution or the step execution does not exist for the calling tenant.</summary>
    NotFound
}

/// <summary>
/// Result of <see cref="IWorkflowExecutionOrchestratorService.ScheduleStepRetryAsync"/>, carrying the
/// updated step execution when a retry was scheduled.
/// </summary>
public sealed record ScheduleStepRetryResult(StepRetryStatus Status, WorkflowStepExecution? StepExecution);