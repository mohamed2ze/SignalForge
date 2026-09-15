using SignalForge.Domain.Models;

namespace SignalForge.Api.Dtos;

/// <summary>
/// Request model for creating a workflow.
/// </summary>
public class CreateWorkflowRequest
{
    /// <summary>
    /// The workflow name.
    /// </summary>
    public string Name { get; set; } = default!;

    /// <summary>
    /// Optional workflow description.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Request model for updating a workflow.
/// </summary>
public class UpdateWorkflowRequest
{
    /// <summary>
    /// The workflow name.
    /// </summary>
    public string Name { get; set; } = default!;

    /// <summary>
    /// Optional workflow description.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Request model for creating a workflow version.
/// </summary>
public class CreateWorkflowVersionRequest
{
    /// <summary>
    /// Optional description for the new version.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Request model for executing a workflow.
/// </summary>
public class ExecuteWorkflowRequest
{
    /// <summary>
    /// The workflow version ID to execute.
    /// </summary>
    public Guid WorkflowVersionId { get; set; }

    /// <summary>
    /// The event ID that triggered this execution.
    /// </summary>
    public Guid EventId { get; set; }
}

/// <summary>
/// Request model for adding a step to a draft workflow version.
/// </summary>
public class AddWorkflowStepRequest
{
    /// <summary>
    /// The 1-based position to insert at. Null appends at the end of the sequence.
    /// </summary>
    public int? StepNumber { get; set; }

    /// <summary>
    /// The step type (one of the <see cref="SignalForge.Domain.Enums.StepType"/> names).
    /// </summary>
    public string StepType { get; set; } = default!;

    /// <summary>
    /// JSON step configuration; validated against the step type's required fields.
    /// </summary>
    public string Configuration { get; set; } = default!;

    /// <summary>
    /// Optional human-readable step name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Optional step description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether the step starts enabled (default true).
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// Request model for updating a step on a draft workflow version (null fields are left untouched).
/// </summary>
public class UpdateWorkflowStepRequest
{
    /// <summary>
    /// Optional new step name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Optional new step description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Optional new JSON configuration (validated against the step type).
    /// </summary>
    public string? Configuration { get; set; }

    /// <summary>
    /// Optional new enabled state.
    /// </summary>
    public bool? IsEnabled { get; set; }
}

/// <summary>
/// Request model for reordering the steps of a draft workflow version.
/// </summary>
public class ReorderWorkflowStepsRequest
{
    /// <summary>
    /// The version's step ids in their new order (each exactly once).
    /// </summary>
    public List<Guid> StepIdsInOrder { get; set; } = new();
}

/// <summary>
/// Response model for workflow data.
/// </summary>
public class WorkflowDto
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<WorkflowVersionDto> Versions { get; set; } = new();

    public static WorkflowDto FromDomain(Workflow workflow)
    {
        return new WorkflowDto
        {
            Id = workflow.Id,
            TenantId = workflow.TenantId,
            Name = workflow.Name,
            Description = workflow.Description,
            IsEnabled = workflow.IsEnabled,
            CreatedAt = workflow.CreatedAt,
            UpdatedAt = workflow.UpdatedAt,
            Versions = workflow.Versions
                .OrderByDescending(v => v.VersionNumber)
                .Select(WorkflowVersionDto.FromDomain)
                .ToList()
        };
    }
}

/// <summary>
/// Response model for workflow summary data (list view).
/// </summary>
public class WorkflowSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }
    public int VersionCount { get; set; }
    public int LatestVersionNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public static WorkflowSummaryDto FromDomain(Workflow workflow)
    {
        return new WorkflowSummaryDto
        {
            Id = workflow.Id,
            Name = workflow.Name,
            Description = workflow.Description,
            IsEnabled = workflow.IsEnabled,
            VersionCount = workflow.Versions.Count,
            LatestVersionNumber = workflow.Versions.Count == 0
                ? 0
                : workflow.Versions.Max(v => v.VersionNumber),
            CreatedAt = workflow.CreatedAt,
            UpdatedAt = workflow.UpdatedAt
        };
    }
}

/// <summary>
/// Response model for workflow version data.
/// </summary>
public class WorkflowVersionDto
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public int VersionNumber { get; set; }
    public string? Description { get; set; }
    public bool IsPublished { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public List<WorkflowStepDto> Steps { get; set; } = new();

    public static WorkflowVersionDto FromDomain(WorkflowVersion version)
    {
        return new WorkflowVersionDto
        {
            Id = version.Id,
            WorkflowId = version.WorkflowId,
            VersionNumber = version.VersionNumber,
            Description = version.Description,
            IsPublished = version.IsPublished,
            CreatedAt = version.CreatedAt,
            PublishedAt = version.PublishedAt,
            Steps = version.Steps
                .OrderBy(s => s.StepNumber)
                .Select(WorkflowStepDto.FromDomain)
                .ToList()
        };
    }
}

/// <summary>
/// Response model for workflow step data.
/// </summary>
public class WorkflowStepDto
{
    public Guid Id { get; set; }
    public Guid WorkflowVersionId { get; set; }
    public int StepNumber { get; set; }
    public string StepType { get; set; } = default!;
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string Configuration { get; set; } = default!;
    public bool IsEnabled { get; set; }

    public static WorkflowStepDto FromDomain(WorkflowStep step)
    {
        return new WorkflowStepDto
        {
            Id = step.Id,
            WorkflowVersionId = step.WorkflowVersionId,
            StepNumber = step.StepNumber,
            StepType = step.StepType,
            Name = step.Name,
            Description = step.Description,
            Configuration = step.Configuration,
            IsEnabled = step.IsEnabled
        };
    }
}

/// <summary>
/// Response model for workflow execution data.
/// </summary>
public class WorkflowExecutionDto
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid WorkflowVersionId { get; set; }
    public Guid EventId { get; set; }
    public Guid TenantId { get; set; }
    public string Status { get; set; } = default!;
    public int CurrentStepNumber { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }

    public static WorkflowExecutionDto FromDomain(WorkflowExecution execution)
    {
        return new WorkflowExecutionDto
        {
            Id = execution.Id,
            WorkflowId = execution.WorkflowId,
            WorkflowVersionId = execution.WorkflowVersionId,
            EventId = execution.EventId,
            TenantId = execution.TenantId,
            Status = execution.Status,
            CurrentStepNumber = execution.CurrentStepNumber,
            StartedAt = execution.StartedAt,
            CompletedAt = execution.CompletedAt,
            RetryCount = execution.RetryCount,
            ErrorMessage = execution.ErrorMessage
        };
    }
}

/// <summary>
/// Response model for workflow step execution data.
/// </summary>
public class WorkflowStepExecutionDto
{
    public Guid Id { get; set; }
    public Guid WorkflowExecutionId { get; set; }
    public Guid WorkflowStepId { get; set; }
    public int StepNumber { get; set; }
    public string Status { get; set; } = default!;
    public int AttemptNumber { get; set; }
    public int MaxAttempts { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Output { get; set; }

    public static WorkflowStepExecutionDto FromDomain(WorkflowStepExecution stepExecution)
    {
        return new WorkflowStepExecutionDto
        {
            Id = stepExecution.Id,
            WorkflowExecutionId = stepExecution.WorkflowExecutionId,
            WorkflowStepId = stepExecution.WorkflowStepId,
            StepNumber = stepExecution.StepNumber,
            Status = stepExecution.Status,
            AttemptNumber = stepExecution.AttemptNumber,
            MaxAttempts = stepExecution.MaxAttempts,
            StartedAt = stepExecution.StartedAt,
            CompletedAt = stepExecution.CompletedAt,
            NextRetryAt = stepExecution.NextRetryAt,
            ErrorMessage = stepExecution.ErrorMessage,
            Output = stepExecution.Output
        };
    }
}