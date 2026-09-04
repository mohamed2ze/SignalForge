using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SignalForge.Application.Data;
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
    }
}