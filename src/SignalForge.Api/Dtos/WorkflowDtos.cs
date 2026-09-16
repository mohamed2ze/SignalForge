using SignalForge.Domain.Models;

namespace SignalForge.Api.Dtos;

public class CreateWorkflowRequest
{
    public string Name { get; set; } = default!;

    public string? Description { get; set; }
}

public class UpdateWorkflowRequest
{
    public string Name { get; set; } = default!;

    public string? Description { get; set; }
}

public class CreateWorkflowVersionRequest
{
    public string? Description { get; set; }
}

public class ExecuteWorkflowRequest
{
    public Guid WorkflowVersionId { get; set; }

    public Guid EventId { get; set; }
}

public class AddWorkflowStepRequest
{
    public int? StepNumber { get; set; }

    public string StepType { get; set; } = default!;

    public string Configuration { get; set; } = default!;

    public string? Name { get; set; }

    public string? Description { get; set; }

    public bool IsEnabled { get; set; } = true;
}

public class UpdateWorkflowStepRequest
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Configuration { get; set; }

    public bool? IsEnabled { get; set; }
}

public class ReorderWorkflowStepsRequest
{
    public List<Guid> StepIdsInOrder { get; set; } = new();
}

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