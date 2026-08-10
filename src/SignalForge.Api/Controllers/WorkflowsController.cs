using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;
using SignalForge.Domain.ValueObjects;

namespace SignalForge.Api.Controllers;

/// <summary>
/// Controller for managing workflows, workflow versions, and workflow executions.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class WorkflowsController : ControllerBase
{
    private readonly IWorkflowExecutionOrchestratorService _workflowOrchestrator;
    private readonly IWorkflowService _workflowService;
    private readonly ILogger<WorkflowsController> _logger;

    public WorkflowsController(
        IWorkflowExecutionOrchestratorService workflowOrchestrator,
        IWorkflowService workflowService,
        ILogger<WorkflowsController> logger)
    {
        _workflowOrchestrator = workflowOrchestrator;
        _workflowService = workflowService;
        _logger = logger;
    }

    /// <summary>
    /// Lists all workflows for the caller's tenant.
    /// </summary>
    /// <returns>The list of workflows</returns>
    [HttpGet]
    [ProducesResponseType(typeof(List<WorkflowSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<WorkflowSummaryDto>>> GetWorkflows()
    {
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem);

        var workflows = await _workflowService.GetWorkflowsAsync(tenantId);
        return Ok(workflows.Select(WorkflowSummaryDto.FromDomain).ToList());
    }

    /// <summary>
    /// Creates a new workflow (with its initial draft version).
    /// </summary>
    /// <param name="request">The workflow creation request</param>
    /// <returns>The created workflow</returns>
    [HttpPost]
    [ProducesResponseType(typeof(WorkflowDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<WorkflowDto>> CreateWorkflow([FromBody] CreateWorkflowRequest request)
    {
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Workflow name is required",
                Status = StatusCodes.Status400BadRequest,
                Detail = "The workflow name must be provided"
            });
        }

        var workflow = await _workflowService.CreateWorkflowAsync(tenantId, request.Name, request.Description);

        _logger.LogInformation("Workflow {WorkflowId} created for tenant {TenantId}", workflow.Id, tenantId);

        return CreatedAtAction(nameof(GetWorkflowById), new { workflowId = workflow.Id },
            WorkflowDto.FromDomain(workflow));
    }

    /// <summary>
    /// Gets a workflow (with its versions and steps).
    /// </summary>
    /// <param name="workflowId">The workflow ID</param>
    /// <returns>The workflow if found</returns>
    [HttpGet("{workflowId:guid}")]
    [ProducesResponseType(typeof(WorkflowDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkflowDto>> GetWorkflowById(Guid workflowId)
    {
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem);

        var workflow = await _workflowService.GetWorkflowByIdAsync(workflowId, tenantId);
        if (workflow == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Workflow not found",
                Status = StatusCodes.Status404NotFound,
                Detail = $"Workflow with ID {workflowId} not found for this tenant"
            });
        }

        return Ok(WorkflowDto.FromDomain(workflow));
    }

    /// <summary>
    /// Updates a workflow's name and description.
    /// </summary>
    /// <param name="workflowId">The workflow ID</param>
    /// <param name="request">The workflow update request</param>
    /// <returns>The updated workflow</returns>
    [HttpPut("{workflowId:guid}")]
    [ProducesResponseType(typeof(WorkflowDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkflowDto>> UpdateWorkflow(
        Guid workflowId,
        [FromBody] UpdateWorkflowRequest request)
    {
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Workflow name is required",
                Status = StatusCodes.Status400BadRequest,
                Detail = "The workflow name must be provided"
            });
        }

        var workflow = await _workflowService.UpdateWorkflowAsync(workflowId, tenantId, request.Name, request.Description);
        if (workflow == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Workflow not found",
                Status = StatusCodes.Status404NotFound,
                Detail = $"Workflow with ID {workflowId} not found for this tenant"
            });
        }

        return Ok(WorkflowDto.FromDomain(workflow));
    }

    /// <summary>
    /// Soft-deletes a workflow.
    /// </summary>
    /// <param name="workflowId">The workflow ID</param>
    /// <returns>No content on success</returns>
    [HttpDelete("{workflowId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteWorkflow(Guid workflowId)
    {
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem);

        var deleted = await _workflowService.DeleteWorkflowAsync(workflowId, tenantId);
        if (!deleted)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Workflow not found",
                Status = StatusCodes.Status404NotFound,
                Detail = $"Workflow with ID {workflowId} not found for this tenant"
            });
        }

        return NoContent();
    }

    /// <summary>
    /// Creates a new draft version of a workflow (a copy of the latest version, version number incremented).
    /// </summary>
    /// <param name="workflowId">The workflow ID</param>
    /// <param name="request">The version creation request</param>
    /// <returns>The created draft version</returns>
    [HttpPost("{workflowId:guid}/versions")]
    [ProducesResponseType(typeof(WorkflowVersionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkflowVersionDto>> CreateWorkflowVersion(
        Guid workflowId,
        [FromBody] CreateWorkflowVersionRequest request)
    {
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem);

        var version = await _workflowService.CreateVersionAsync(workflowId, tenantId, request.Description);
        if (version == null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Workflow not found",
                Status = StatusCodes.Status404NotFound,
                Detail = $"Workflow with ID {workflowId} not found for this tenant"
            });
        }

        return CreatedAtAction(
            nameof(GetWorkflowById),
            new { workflowId },
            WorkflowVersionDto.FromDomain(version));
    }

    /// <summary>
    /// Publishes a draft workflow version, making the workflow enabled.
    /// </summary>
    /// <param name="workflowId">The workflow ID</param>
    /// <param name="versionId">The version ID to publish</param>
    /// <returns>The published version</returns>
    [HttpPost("{workflowId:guid}/versions/{versionId:guid}/publish")]
    [ProducesResponseType(typeof(WorkflowVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkflowVersionDto>> PublishWorkflowVersion(
        Guid workflowId,
        Guid versionId)
    {
        if (!TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem);

        try
        {
            var version = await _workflowService.PublishVersionAsync(workflowId, versionId, tenantId);
            if (version == null)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Workflow or version not found",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Workflow {workflowId} or version {versionId} not found for this tenant"
                });
            }

            return Ok(WorkflowVersionDto.FromDomain(version));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Cannot publish version",
                Status = StatusCodes.Status400BadRequest,
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Starts a workflow execution.
    /// </summary>
    /// <param name="request">The workflow execution request</param>
    /// <returns>The created workflow execution</returns>
    [HttpPost("{workflowId:guid}/execute")]
    [ProducesResponseType(typeof(WorkflowExecutionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<WorkflowExecutionDto>> ExecuteWorkflow(
        Guid workflowId,
        [FromBody] ExecuteWorkflowRequest request)
    {
        try
        {
            if (!TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem);

            // Validate request
            if (request.WorkflowVersionId == Guid.Empty)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Workflow version ID is required",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = "The workflow version ID must be provided"
                });
            }

            if (request.EventId == Guid.Empty)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Event ID is required",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = "The event ID must be provided"
                });
            }

            // Start the workflow execution
            var execution = await _workflowOrchestrator.StartWorkflowExecutionAsync(
                workflowId,
                request.WorkflowVersionId,
                request.EventId,
                tenantId);

            _logger.LogInformation("Workflow execution started: {ExecutionId} for workflow {WorkflowId}",
                execution.Id, workflowId);

            return CreatedAtAction(
                nameof(GetWorkflowExecutionById),
                new { workflowId, executionId = execution.Id },
                WorkflowExecutionDto.FromDomain(execution));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid operation executing workflow {WorkflowId}", workflowId);
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid request",
                Status = StatusCodes.Status400BadRequest,
                Detail = ex.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing workflow {WorkflowId}", workflowId);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal server error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = "An unexpected error occurred while executing the workflow"
            });
        }
    }

    /// <summary>
    /// Gets a workflow execution by its ID.
    /// </summary>
    /// <param name="workflowId">The parent workflow ID</param>
    /// <param name="executionId">The workflow execution ID</param>
    /// <returns>The workflow execution if found</returns>
    [HttpGet("{workflowId:guid}/executions/{executionId:guid}")]
    [ProducesResponseType(typeof(WorkflowExecutionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkflowExecutionDto>> GetWorkflowExecutionById(
        Guid workflowId,
        Guid executionId)
    {
        try
        {
            if (!TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem);

            var execution = await _workflowOrchestrator.GetWorkflowExecutionByIdAsync(executionId, tenantId);
            if (execution == null)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Workflow execution not found",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Workflow execution with ID {executionId} not found for this tenant"
                });
            }

            return Ok(WorkflowExecutionDto.FromDomain(execution));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving workflow execution {ExecutionId}", executionId);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal server error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = "An unexpected error occurred while retrieving the workflow execution"
            });
        }
    }

    /// <summary>
    /// Gets the step executions for a workflow execution.
    /// </summary>
    /// <param name="executionId">The workflow execution ID</param>
    /// <returns>The step executions for the workflow execution</returns>
    [HttpGet("{executionId:guid}/steps")]
    [ProducesResponseType(typeof(List<WorkflowStepExecutionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<WorkflowStepExecutionDto>>> GetWorkflowExecutionSteps(Guid executionId)
    {
        try
        {
            if (!TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem);

            var execution = await _workflowOrchestrator.GetWorkflowExecutionByIdAsync(executionId, tenantId);
            if (execution == null)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Workflow execution not found",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Workflow execution with ID {executionId} not found for this tenant"
                });
            }

            var stepExecutions = execution.StepExecutions
                .OrderBy(se => se.StepNumber)
                .Select(WorkflowStepExecutionDto.FromDomain)
                .ToList();

            return Ok(stepExecutions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving steps for workflow execution {ExecutionId}", executionId);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal server error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = "An unexpected error occurred while retrieving workflow execution steps"
            });
        }
    }

    /// <summary>
    /// Retries a failed workflow step execution.
    /// </summary>
    /// <param name="executionId">The workflow execution ID</param>
    /// <param name="stepExecutionId">The step execution ID</param>
    /// <returns>True if the step execution was retried, false otherwise</returns>
    [HttpPost("{executionId:guid}/steps/{stepExecutionId:guid}/retry")]
    [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<bool>> RetryStepExecution(
        Guid executionId,
        Guid stepExecutionId)
    {
        try
        {
            if (!TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem);

            // In a real implementation, we would check if the step is eligible for retry
            // For now, we'll return false as this would require more complex orchestration
            return BadRequest(new ProblemDetails
            {
                Title = "Retry not implemented via API",
                Status = StatusCodes.Status400BadRequest,
                Detail = "Step retry functionality is handled automatically by the workflow engine"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrying step execution {StepExecutionId} for execution {ExecutionId}",
                stepExecutionId, executionId);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Title = "Internal server error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = "An unexpected error occurred while retrying the step execution"
            });
        }
    }

    private bool TryGetTenantId(out Guid tenantId)
    {
        var tenantIdClaim = User.FindFirst("tenant_id");
        if (tenantIdClaim == null || !Guid.TryParse(tenantIdClaim.Value, out tenantId))
        {
            tenantId = Guid.Empty;
            return false;
        }
        return true;
    }

    private ProblemDetails TenantProblem => new ProblemDetails
    {
        Title = "Invalid tenant information",
        Status = StatusCodes.Status401Unauthorized,
        Detail = "Unable to determine tenant from authentication token"
    };
}

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