using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SignalForge.Api.Dtos;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;
using static SignalForge.Api.Controllers.ApiControllerExtensions;

namespace SignalForge.Api.Controllers;

/// <summary>
/// Controller for managing workflows, workflow versions, and workflow executions.
/// </summary>
[ApiController]
[Route("api/workflows")]
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
        if (!this.TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem());

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
        if (!this.TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem());

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
        if (!this.TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem());

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
        if (!this.TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem());

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
        if (!this.TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem());

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
        if (!this.TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem());

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
        if (!this.TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem());

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
            if (!this.TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem());

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
            return this.InternalServerError("An unexpected error occurred while executing the workflow");
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
            if (!this.TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem());

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
            return this.InternalServerError("An unexpected error occurred while retrieving the workflow execution");
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
            if (!this.TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem());

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
            return this.InternalServerError("An unexpected error occurred while retrieving workflow execution steps");
        }
    }

    /// <summary>
    /// Retries a failed workflow step execution: re-schedules the step with the same exponential
    /// backoff the worker uses, returning the updated step execution state.
    /// </summary>
    /// <param name="executionId">The workflow execution ID</param>
    /// <param name="stepExecutionId">The step execution ID</param>
    /// <returns>The updated step execution when the retry was scheduled</returns>
    [HttpPost("{executionId:guid}/steps/{stepExecutionId:guid}/retry")]
    [ProducesResponseType(typeof(WorkflowStepExecutionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<WorkflowStepExecutionDto>> RetryStepExecution(
        Guid executionId,
        Guid stepExecutionId)
    {
        try
        {
            if (!this.TryGetTenantId(out var tenantId))
                return Unauthorized(TenantProblem());

            var result = await _workflowOrchestrator.ScheduleStepRetryAsync(
                executionId, stepExecutionId, tenantId);

            if (result.Status == StepRetryStatus.NotFound)
            {
                return NotFound(new ProblemDetails
                {
                    Title = "Workflow step execution not found",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Workflow step execution with ID {stepExecutionId} not found for this tenant"
                });
            }

            if (result.Status != StepRetryStatus.Scheduled)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Step execution cannot be retried",
                    Status = StatusCodes.Status409Conflict,
                    Detail = "The step execution is currently running, waiting for its retry window, or has exhausted its attempts"
                });
            }

            return Ok(WorkflowStepExecutionDto.FromDomain(result.StepExecution!));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrying step execution {StepExecutionId} for execution {ExecutionId}",
                stepExecutionId, executionId);
            return this.InternalServerError("An unexpected error occurred while retrying the step execution");
        }
    }

    // ---------- Draft step authoring (C2: remove the last SQL-only surface) ----------

    /// <summary>
    /// Adds a step to a draft workflow version. Inserting at an explicit position shifts the
    /// following steps; omitting <c>stepNumber</c> appends at the end.
    /// </summary>
    /// <param name="workflowId">The workflow ID</param>
    /// <param name="versionId">The draft version ID</param>
    /// <param name="request">The step to add</param>
    /// <returns>The created step</returns>
    [HttpPost("{workflowId:guid}/versions/{versionId:guid}/steps")]
    [ProducesResponseType(typeof(WorkflowStepDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<ActionResult<WorkflowStepDto>> AddWorkflowStep(
        Guid workflowId,
        Guid versionId,
        [FromBody] AddWorkflowStepRequest request)
        => ExecuteStepMutationAsync(
            tenant => _workflowService.AddStepAsync(
                versionId, tenant,
                new AddStepCommand(
                    request.StepNumber,
                    request.StepType,
                    request.Configuration,
                    request.Name,
                    request.Description,
                    request.IsEnabled)),
            created: true);

    /// <summary>
    /// Updates a step on a draft version (null request fields are left untouched). The
    /// configuration, when provided, is re-validated against the step type.
    /// </summary>
    /// <param name="workflowId">The workflow ID</param>
    /// <param name="versionId">The draft version ID</param>
    /// <param name="stepId">The step ID</param>
    /// <param name="request">The fields to update</param>
    /// <returns>The updated step</returns>
    [HttpPut("{workflowId:guid}/versions/{versionId:guid}/steps/{stepId:guid}")]
    [ProducesResponseType(typeof(WorkflowStepDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<ActionResult<WorkflowStepDto>> UpdateWorkflowStep(
        Guid workflowId,
        Guid versionId,
        Guid stepId,
        [FromBody] UpdateWorkflowStepRequest request)
        => ExecuteStepMutationAsync(
            tenant => _workflowService.UpdateStepAsync(
                versionId, tenant, stepId,
                new UpdateStepCommand(request.Name, request.Description, request.Configuration, request.IsEnabled)),
            created: false);

    /// <summary>
    /// Enables a step on a draft version.
    /// </summary>
    [HttpPost("{workflowId:guid}/versions/{versionId:guid}/steps/{stepId:guid}/enable")]
    [ProducesResponseType(typeof(WorkflowStepDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<ActionResult<WorkflowStepDto>> EnableWorkflowStep(Guid workflowId, Guid versionId, Guid stepId)
        => ExecuteStepMutationAsync(
            tenant => _workflowService.SetStepEnabledAsync(versionId, tenant, stepId, enabled: true),
            created: false);

    /// <summary>
    /// Disables a step on a draft version. Disabled steps are skipped by the execution engine.
    /// </summary>
    [HttpPost("{workflowId:guid}/versions/{versionId:guid}/steps/{stepId:guid}/disable")]
    [ProducesResponseType(typeof(WorkflowStepDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<ActionResult<WorkflowStepDto>> DisableWorkflowStep(Guid workflowId, Guid versionId, Guid stepId)
        => ExecuteStepMutationAsync(
            tenant => _workflowService.SetStepEnabledAsync(versionId, tenant, stepId, enabled: false),
            created: false);

    /// <summary>
    /// Removes a step from a draft version and renumbers the remaining steps contiguously.
    /// </summary>
    /// <returns>No content on success</returns>
    [HttpDelete("{workflowId:guid}/versions/{versionId:guid}/steps/{stepId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteWorkflowStep(Guid workflowId, Guid versionId, Guid stepId)
    {
        if (!this.TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem());

        try
        {
            var removed = await _workflowService.RemoveStepAsync(versionId, tenantId, stepId);
            if (!removed)
                return NotFound(StepNotFoundProblem(stepId));

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Cannot modify published version",
                Status = StatusCodes.Status400BadRequest,
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Reorders the steps of a draft version: the request must list every step id exactly once.
    /// </summary>
    /// <returns>The reordered version (with its steps)</returns>
    [HttpPut("{workflowId:guid}/versions/{versionId:guid}/steps/reorder")]
    [ProducesResponseType(typeof(WorkflowVersionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WorkflowVersionDto>> ReorderWorkflowSteps(
        Guid workflowId,
        Guid versionId,
        [FromBody] ReorderWorkflowStepsRequest request)
    {
        if (!this.TryGetTenantId(out var tenantId))
            return Unauthorized(TenantProblem());

        try
        {
            var reordered = await _workflowService.ReorderStepsAsync(versionId, tenantId, request.StepIdsInOrder);
            if (!reordered)
                return NotFound(VersionNotFoundProblem(workflowId, versionId));

            var version = await _workflowService.GetWorkflowVersionAsync(versionId, tenantId);
            return Ok(WorkflowVersionDto.FromDomain(version!));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Cannot reorder steps",
                Status = StatusCodes.Status400BadRequest,
                Detail = ex.Message
            });
        }
    }

    /// <summary>
    /// Shared mutation plumbing for step authoring: resolves the tenant, delegates to a service
    /// command, and maps null/validation/published-version outcomes to 404/400 responses while
    /// keeping the 201/success bodies consistent.
    /// </summary>
    private async Task<ActionResult<WorkflowStepDto>> ExecuteStepMutationAsync(
        Func<Guid, Task<WorkflowStep?>> operation,
        bool created)
    {
        if (!this.TryGetTenantId(out var tenantId) || tenantId == Guid.Empty)
            return Unauthorized(TenantProblem());

        try
        {
            var step = await operation(tenantId);
            if (step == null)
                return NotFound(new ProblemDetails
                {
                    Title = "Workflow, version, or step not found",
                    Status = StatusCodes.Status404NotFound,
                    Detail = "The workflow version or step was not found for this tenant"
                });

            var dto = WorkflowStepDto.FromDomain(step);
            return created ? Created(AbsoluteUrlToStep(), dto) : Ok(dto);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Cannot modify workflow step",
                Status = StatusCodes.Status400BadRequest,
                Detail = ex.Message
            });
        }
    }

    private static ProblemDetails VersionNotFoundProblem(Guid workflowId, Guid versionId)
        => new()
        {
            Title = "Workflow or version not found",
            Status = StatusCodes.Status404NotFound,
            Detail = $"Workflow {workflowId} or version {versionId} not found for this tenant"
        };

    private static ProblemDetails StepNotFoundProblem(Guid stepId)
        => new()
        {
            Title = "Workflow step not found",
            Status = StatusCodes.Status404NotFound,
            Detail = $"Workflow step with ID {stepId} not found for this tenant"
        };

    private string AbsoluteUrlToStep()
    {
        var versionId = HttpContext.Request.RouteValues["versionId"];
        var workflowId = HttpContext.Request.RouteValues["workflowId"];
        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/workflows/{workflowId}/versions/{versionId}/steps/{HttpContext.Request.RouteValues["stepId"]}";
    }
}
