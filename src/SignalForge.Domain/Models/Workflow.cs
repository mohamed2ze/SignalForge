namespace SignalForge.Domain.Models;

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

    public static Workflow Create(Guid tenantId, string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Workflow name cannot be empty", nameof(name));

        return new Workflow(Guid.NewGuid(), tenantId, name.Trim(), description?.Trim());
    }

    public void UpdateInfo(string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Workflow name cannot be empty", nameof(name));

        Name = name.Trim();
        Description = description?.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    public void Enable()
    {
        IsEnabled = true;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Disable()
    {
        IsEnabled = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Delete()
    {
        IsEnabled = false;
        DeletedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public WorkflowVersion CreateDraftVersion(int versionNumber, string? description = null)
    {
        var version = WorkflowVersion.Create(Id, versionNumber, description);
        Versions.Add(version);
        return version;
    }

    public WorkflowVersion? GetLatestPublishedVersion()
    {
        return Versions
            .Where(v => v.IsPublished)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefault();
    }

    public WorkflowVersion? GetLatestVersion()
    {
        return Versions
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefault();
    }
}