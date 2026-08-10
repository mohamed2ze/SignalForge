using System.Threading;
using System.Threading.Tasks;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services
{
    /// <summary>
    /// Service for managing workflow definitions and their version lifecycle.
    /// All operations are tenant-scoped: a workflow is only ever visible to the tenant that owns it.
    /// </summary>
    public interface IWorkflowService
    {
        /// <summary>
        /// Lists all non-deleted workflows for a tenant.
        /// </summary>
        Task<List<Workflow>> GetWorkflowsAsync(Guid tenantId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a workflow (with its versions and steps) for a tenant.
        /// Returns null if not found or not owned by the tenant.
        /// </summary>
        Task<Workflow?> GetWorkflowByIdAsync(Guid workflowId, Guid tenantId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a workflow and its initial draft version (v1).
        /// </summary>
        Task<Workflow> CreateWorkflowAsync(Guid tenantId, string name, string? description = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates a workflow's name/description.
        /// Returns null if not found or not owned by the tenant.
        /// </summary>
        Task<Workflow?> UpdateWorkflowAsync(Guid workflowId, Guid tenantId, string name, string? description = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Soft-deletes a workflow.
        /// Returns false if not found or not owned by the tenant.
        /// </summary>
        Task<bool> DeleteWorkflowAsync(Guid workflowId, Guid tenantId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a new draft version of a workflow by copying the latest version (steps included)
        /// and incrementing the version number.
        /// Returns null if the workflow is not found or not owned by the tenant.
        /// </summary>
        Task<WorkflowVersion?> CreateVersionAsync(Guid workflowId, Guid tenantId, string? description = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Publishes a draft version, unpublishing any other published version and enabling the workflow.
        /// Returns null if the workflow or version is not found / not owned by the tenant.
        /// Throws InvalidOperationException if the version is already published.
        /// </summary>
        Task<WorkflowVersion?> PublishVersionAsync(Guid workflowId, Guid versionId, Guid tenantId, CancellationToken cancellationToken = default);
    }
}