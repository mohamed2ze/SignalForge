using System.Collections.Generic;
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
        Task<List<Workflow>> GetWorkflowsAsync(Guid tenantId, CancellationToken cancellationToken = default);

        Task<Workflow?> GetWorkflowByIdAsync(Guid workflowId, Guid tenantId, CancellationToken cancellationToken = default);

        Task<WorkflowVersion?> GetWorkflowVersionAsync(Guid versionId, Guid tenantId, CancellationToken cancellationToken = default);

        Task<Workflow> CreateWorkflowAsync(Guid tenantId, string name, string? description = null, CancellationToken cancellationToken = default);

        Task<Workflow?> UpdateWorkflowAsync(Guid workflowId, Guid tenantId, string name, string? description = null, CancellationToken cancellationToken = default);

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

        /// <summary>
        /// Adds a step to a draft version. When an explicit position collides with an existing step,
        /// subsequent steps are shifted down. Returns null when the workflow/version is not found or
        /// not owned by the tenant; throws InvalidOperationException when the version is published
        /// (published versions are immutable), the step type is unknown, or the configuration fails
        /// per-type validation.
        /// </summary>
        Task<WorkflowStep?> AddStepAsync(Guid workflowVersionId, Guid tenantId, AddStepCommand command, CancellationToken cancellationToken = default);

        /// <summary>
        /// Updates a step on a draft version (null command fields are left untouched).
        /// Returns null when the workflow/version/step is not found or not owned by the tenant;
        /// throws InvalidOperationException when the version is published or the new configuration
        /// fails per-type validation.
        /// </summary>
        Task<WorkflowStep?> UpdateStepAsync(Guid workflowVersionId, Guid tenantId, Guid stepId, UpdateStepCommand command, CancellationToken cancellationToken = default);

        /// <summary>Enables/disables a step on a draft. Null when not found/owned; throws when published.</summary>
        Task<WorkflowStep?> SetStepEnabledAsync(Guid workflowVersionId, Guid tenantId, Guid stepId, bool enabled, CancellationToken cancellationToken = default);

        /// <summary>Removes a step, renumbering the rest contiguously. False when not found/owned; throws when published.</summary>
        Task<bool> RemoveStepAsync(Guid workflowVersionId, Guid tenantId, Guid stepId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Applies a new order to the draft version's steps (each id must belong to the version).
        /// Returns false when the workflow/version is not found / not owned; throws
        /// InvalidOperationException when the version is published or the order does not match the
        /// version's step set.
        /// </summary>
        Task<bool> ReorderStepsAsync(Guid workflowVersionId, Guid tenantId, IReadOnlyList<Guid> stepIdsInOrder, CancellationToken cancellationToken = default);
    }
}