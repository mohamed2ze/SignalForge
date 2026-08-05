namespace SignalForge.Domain.Models;

/// <summary>
/// Represents a workflow definition that processes events.
/// Workflows contain steps that are executed in sequence.
/// </summary>
public class Workflow
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }
    public bool IsEnabled { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public DateTime? DeletedAt { get; private set; }

    // Navigation properties
    public Tenant Tenant { get; private set; } = default!;
    public ICollection<WorkflowVersion> Versions { get; private set; } = new List<WorkflowVersion>();
    public ICollection<WorkflowExecution> Executions { get; private set; } = new List<WorkflowExecution>();

    private Workflow() { } // For EF Core

    private Workflow(Guid id, Guid tenantId, string name, string? description = null)
    {
        Id = id;
        TenantId = tenantId;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description;
        IsEnabled = false; // Workflows start disabled until published
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a new workflow.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="name">The workflow name</param>
    /// <param name="description">Optional description</param>
    /// <returns>A new Workflow instance</returns>
    public static Workflow Create(Guid tenantId, string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Workflow name cannot be empty", nameof(name));

        return new Workflow(Guid.NewGuid(), tenantId, name.Trim(), description?.Trim());
    }

    /// <summary>
    /// Updates the workflow information.
    /// </summary>
    /// <param name="name">The new workflow name</param>
    /// <param name="description">Optional new description</param>
    public void UpdateInfo(string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Workflow name cannot be empty", nameof(name));

        Name = name.Trim();
        Description = description?.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Enables the workflow.
    /// </summary>
    public void Enable()
    {
        IsEnabled = true;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Disables the workflow.
    /// </summary>
    public void Disable()
    {
        IsEnabled = false;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Deletes the workflow (soft delete).
    /// </summary>
    public void Delete()
    {
        IsEnabled = false;
        DeletedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a new draft version of this workflow and attaches it to the version list.
    /// </summary>
    /// <param name="versionNumber">The version number for the new draft</param>
    /// <param name="description">Optional version description</param>
    /// <returns>The created draft version</returns>
    public WorkflowVersion CreateDraftVersion(int versionNumber, string? description = null)
    {
        var version = WorkflowVersion.Create(Id, versionNumber, description);
        Versions.Add(version);
        return version;
    }

    /// <summary>
    /// Gets the latest published version of the workflow.
    /// </summary>
    /// <returns>The latest published workflow version, or null if none published</returns>
    public WorkflowVersion? GetLatestPublishedVersion()
    {
        return Versions
            .Where(v => v.IsPublished)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefault();
    }

    /// <summary>
    /// Gets the latest version (published or draft) of the workflow.
    /// </returns>
    public WorkflowVersion? GetLatestVersion()
    {
        return Versions
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefault();
    }
}