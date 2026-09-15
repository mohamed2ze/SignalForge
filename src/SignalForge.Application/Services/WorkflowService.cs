using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SignalForge.Application.Data;
using SignalForge.Application.Validation;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services
{
    /// <summary>
    /// Implementation of the workflow lifecycle service.
    /// </summary>
    public class WorkflowService : IWorkflowService
    {
        private readonly ISignalForgeDbContext _dbContext;
        private readonly ILogger<WorkflowService> _logger;

        public WorkflowService(ISignalForgeDbContext dbContext, ILogger<WorkflowService> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<List<Workflow>> GetWorkflowsAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            return await _dbContext.Workflows
                .Where(w => w.TenantId == tenantId && w.DeletedAt == null)
                .Include(w => w.Versions)
                .OrderByDescending(w => w.CreatedAt)
                .ToListAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task<Workflow?> GetWorkflowByIdAsync(
            Guid workflowId,
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            return await _dbContext.Workflows
                .Where(w => w.Id == workflowId && w.TenantId == tenantId && w.DeletedAt == null)
                .Include(w => w.Versions)
                    .ThenInclude(v => v.Steps)
                .FirstOrDefaultAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task<Workflow> CreateWorkflowAsync(
            Guid tenantId,
            string name,
            string? description = null,
            CancellationToken cancellationToken = default)
        {
            var workflow = Workflow.Create(tenantId, name, description);
            // Every workflow starts with an initial (draft) version.
            workflow.CreateDraftVersion(1);

            _dbContext.Workflows.Add(workflow);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Workflow {WorkflowId} created for tenant {TenantId}",
                workflow.Id, tenantId);
            return workflow;
        }

        /// <inheritdoc />
        public async Task<Workflow?> UpdateWorkflowAsync(
            Guid workflowId,
            Guid tenantId,
            string name,
            string? description = null,
            CancellationToken cancellationToken = default)
        {
            var workflow = await GetWorkflowByIdAsync(workflowId, tenantId, cancellationToken);
            if (workflow == null)
                return null;

            workflow.UpdateInfo(name, description);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Workflow {WorkflowId} updated for tenant {TenantId}", workflowId, tenantId);
            return workflow;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteWorkflowAsync(Guid workflowId, Guid tenantId, CancellationToken cancellationToken = default)
        {
            var workflow = await _dbContext.Workflows
                .FirstOrDefaultAsync(w => w.Id == workflowId && w.TenantId == tenantId && w.DeletedAt == null, cancellationToken);

            if (workflow == null)
                return false;

            workflow.Delete();
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Workflow {WorkflowId} deleted for tenant {TenantId}", workflowId, tenantId);
            return true;
        }

        /// <inheritdoc />
        public Task<WorkflowVersion?> GetWorkflowVersionAsync(
            Guid versionId,
            Guid tenantId,
            CancellationToken cancellationToken = default)
            => _dbContext.WorkflowVersions
                .Include(v => v.Workflow)
                .Include(v => v.Steps)
                .FirstOrDefaultAsync(
                    v => v.Id == versionId &&
                         v.Workflow.TenantId == tenantId &&
                         v.Workflow.DeletedAt == null,
                    cancellationToken);

        /// <inheritdoc />
        public async Task<WorkflowVersion?> CreateVersionAsync(
            Guid workflowId,
            Guid tenantId,
            string? description = null,
            CancellationToken cancellationToken = default)
        {
            var workflow = await GetWorkflowByIdAsync(workflowId, tenantId, cancellationToken);
            if (workflow == null)
                return null;

            var latestVersion = workflow.GetLatestVersion();
            var nextVersionNumber = (latestVersion?.VersionNumber ?? 0) + 1;
            var newVersion = workflow.CreateDraftVersion(nextVersionNumber, description);

            // The new draft is a copy of the latest version, including its steps.
            if (latestVersion != null)
            {
                foreach (var step in latestVersion.Steps.OrderBy(s => s.StepNumber))
                {
                    newVersion.AddStep(
                        step.StepNumber,
                        step.StepType,
                        step.Configuration,
                        step.Name,
                        step.Description,
                        step.IsEnabled);
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Workflow version {VersionId} (v{VersionNumber}) created for workflow {WorkflowId}",
                newVersion.Id, newVersion.VersionNumber, workflowId);
            return newVersion;
        }

        /// <inheritdoc />
        public async Task<WorkflowVersion?> PublishVersionAsync(
            Guid workflowId,
            Guid versionId,
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            var workflow = await GetWorkflowByIdAsync(workflowId, tenantId, cancellationToken);
            if (workflow == null)
                return null;

            var version = workflow.Versions.FirstOrDefault(v => v.Id == versionId);
            if (version == null)
                return null;

            if (version.IsPublished)
                throw new InvalidOperationException("This workflow version is already published");

            // Only one version per workflow may be published at a time.
            foreach (var otherVersion in workflow.Versions.Where(v => v.IsPublished))
            {
                otherVersion.Unpublish();
            }

            version.Publish();
            workflow.Enable();
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Workflow version {VersionId} (v{VersionNumber}) published for workflow {WorkflowId}",
                version.Id, version.VersionNumber, workflowId);
            return version;
        }

        /// <inheritdoc />
        public async Task<WorkflowStep?> AddStepAsync(
            Guid workflowVersionId,
            Guid tenantId,
            AddStepCommand command,
            CancellationToken cancellationToken = default)
        {
            var version = await GetDraftVersionAsync(workflowVersionId, tenantId, cancellationToken);
            if (version == null)
                return null;

            var validationError = WorkflowStepConfigurationValidator.Validate(command.StepType, command.Configuration);
            if (validationError != null)
                throw new InvalidOperationException(validationError);

            var target = command.StepNumber ?? version.Steps.Count + 1;

            // Insert at an explicit position: shift the steps at/after it down so the sequence
            // never duplicates a number.
            foreach (var existing in version.Steps.Where(s => s.StepNumber >= target))
                existing.MoveTo(existing.StepNumber + 1);

            var step = version.AddStep(
                target,
                command.StepType,
                command.Configuration,
                command.Name,
                command.Description,
                command.IsEnabled);

            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Step {StepId} added to version {VersionId} at position {StepNumber}",
                step.Id, workflowVersionId, step.StepNumber);
            return step;
        }

        /// <inheritdoc />
        public async Task<WorkflowStep?> UpdateStepAsync(
            Guid workflowVersionId,
            Guid tenantId,
            Guid stepId,
            UpdateStepCommand command,
            CancellationToken cancellationToken = default)
        {
            var version = await GetDraftVersionAsync(workflowVersionId, tenantId, cancellationToken);
            if (version == null)
                return null;

            var step = version.Steps.FirstOrDefault(s => s.Id == stepId);
            if (step == null)
                return null;

            if (command.Configuration != null)
            {
                var validationError = WorkflowStepConfigurationValidator.Validate(step.StepType, command.Configuration);
                if (validationError != null)
                    throw new InvalidOperationException(validationError);
            }

            step.UpdateInfo(command.Name, command.Description, command.Configuration, command.IsEnabled);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Step {StepId} updated on version {VersionId}", stepId, workflowVersionId);
            return step;
        }

        /// <inheritdoc />
        public async Task<WorkflowStep?> SetStepEnabledAsync(
            Guid workflowVersionId,
            Guid tenantId,
            Guid stepId,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            var version = await GetDraftVersionAsync(workflowVersionId, tenantId, cancellationToken);
            if (version == null)
                return null;

            var step = version.Steps.FirstOrDefault(s => s.Id == stepId);
            if (step == null)
                return null;

            if (enabled)
                step.Enable();
            else
                step.Disable();

            await _dbContext.SaveChangesAsync(cancellationToken);
            return step;
        }

        /// <inheritdoc />
        public async Task<bool> RemoveStepAsync(
            Guid workflowVersionId,
            Guid tenantId,
            Guid stepId,
            CancellationToken cancellationToken = default)
        {
            var version = await GetDraftVersionAsync(workflowVersionId, tenantId, cancellationToken);
            if (version == null)
                return false;

            var removed = version.RemoveStep(stepId);
            if (removed)
            {
                version.RenumberSteps();
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return removed;
        }

        /// <inheritdoc />
        public async Task<bool> ReorderStepsAsync(
            Guid workflowVersionId,
            Guid tenantId,
            IReadOnlyList<Guid> stepIdsInOrder,
            CancellationToken cancellationToken = default)
        {
            var version = await GetDraftVersionAsync(workflowVersionId, tenantId, cancellationToken);
            if (version == null)
                return false;

            try
            {
                version.ApplyStepOrder(stepIdsInOrder);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException(
                    "The step reorder must reference every step of the version exactly once.", e);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        /// <summary>
        /// Loads the specified version when it is owned by the tenant and still a draft.
        /// Returns null when the workflow/version is not found or not owned; throws
        /// <see cref="InvalidOperationException"/> when the version is already published, because
        /// published versions are immutable snapshots used by executions.
        /// </summary>
        private async Task<WorkflowVersion?> GetDraftVersionAsync(
            Guid workflowVersionId,
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            var version = await _dbContext.WorkflowVersions
                .Include(v => v.Workflow)
                .Include(v => v.Steps)
                .FirstOrDefaultAsync(
                    v => v.Id == workflowVersionId &&
                         v.Workflow.TenantId == tenantId &&
                         v.Workflow.DeletedAt == null,
                    cancellationToken);

            if (version == null)
                return null;

            if (version.IsPublished)
                throw new InvalidOperationException(
                    $"Workflow version {workflowVersionId} is published and cannot be modified");

            return version;
        }
    }
}