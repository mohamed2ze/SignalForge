namespace SignalForge.Domain.Models;

/// <summary>
/// Represents a version of a workflow.
/// Workflows can have multiple versions, with only one published at a time.
/// </summary>
public class WorkflowVersion
{
    public Guid Id { get; private set; }
    public Guid WorkflowId { get; private set; }
    public int VersionNumber { get; private set; }
    public string? Description { get; private set; }
    public bool IsPublished { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? PublishedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; } // For concurrency tracking

    // Navigation properties
    public Workflow Workflow { get; private set; } = default!;
    public ICollection<WorkflowStep> Steps { get; private set; } = new List<WorkflowStep>();
    public ICollection<WorkflowExecution> Executions { get; private set; } = new List<WorkflowExecution>();

    private WorkflowVersion() { } // For EF Core

    private WorkflowVersion(
        Guid id,
        Guid workflowId,
        int versionNumber,
        string? description = null)
    {
        Id = id;
        WorkflowId = workflowId;
        VersionNumber = versionNumber;
        Description = description;
        IsPublished = false;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a new workflow version.
    /// </summary>
    /// <param name="workflowId">The workflow ID</param>
    /// <param name="versionNumber">The version number</param>
    /// <param name="description">Optional description</param>
    /// <returns>A new WorkflowVersion instance</returns>
    public static WorkflowVersion Create(Guid workflowId, int versionNumber, string? description = null)
    {
        if (versionNumber <= 0)
            throw new ArgumentException("Version number must be positive", nameof(versionNumber));

        return new WorkflowVersion(Guid.NewGuid(), workflowId, versionNumber, description?.Trim());
    }

    /// <summary>
    /// Publishes this workflow version.
    /// Only one version per workflow can be published at a time.
    /// </summary>
    public void Publish()
    {
        if (Steps.Count == 0)
            throw new InvalidOperationException("Cannot publish a workflow version with no steps");

        IsPublished = true;
        PublishedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Unpublishes this workflow version.
    /// </summary>
    public void Unpublish()
    {
        IsPublished = false;
        PublishedAt = null;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Adds a step to this version.
    /// </summary>
    /// <param name="stepNumber">The step number (order within the version)</param>
    /// <param name="stepType">The type of step</param>
    /// <param name="configuration">JSON configuration for the step</param>
    /// <param name="name">Optional human-readable name</param>
    /// <param name="description">Optional description</param>
    /// <param name="isEnabled">Whether the step is enabled</param>
    /// <returns>The created step</returns>
    public WorkflowStep AddStep(
        int stepNumber,
        string stepType,
        string configuration,
        string? name = null,
        string? description = null,
        bool isEnabled = true)
    {
        var step = WorkflowStep.Create(
            Id,
            stepNumber,
            stepType,
            configuration,
            name,
            description,
            isEnabled);
        Steps.Add(step);
        return step;
    }

    /// <summary>
    /// Updates the version description.
    /// </summary>
    /// <param name="description">The new description</param>
    public void UpdateDescription(string? description)
    {
        Description = description?.Trim();
        UpdatedAt = DateTime.UtcNow;
    }
}