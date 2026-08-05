namespace SignalForge.Domain.Models;

/// <summary>
/// Represents a tenant in the multi-tenant SignalForge platform.
/// Each tenant has isolated data, API keys, workflows, and configuration.
/// </summary>
public class Tenant
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public DateTime? DeletedAt { get; private set; }

    // Navigation properties
    public ICollection<ApiKey> ApiKeys { get; private set; } = new List<ApiKey>();
    public ICollection<Workflow> Workflows { get; private set; } = new List<Workflow>();
    public ICollection<Event> Events { get; private set; } = new List<Event>();

    private Tenant() { } // For EF Core

    private Tenant(Guid id, string name, string? description = null)
    {
        Id = id;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description;
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a new tenant.
    /// </summary>
    /// <param name="name">The tenant name</param>
    /// <param name="description">Optional description</param>
    /// <returns>A new Tenant instance</returns>
    public static Tenant Create(string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tenant name cannot be empty", nameof(name));

        return new Tenant(Guid.NewGuid(), name.Trim(), description?.Trim());
    }

    /// <summary>
    /// Creates a new tenant with a well-known, explicitly supplied id.
    /// Used for seeding data (e.g. a default tenant) where the id must be stable.
    /// </summary>
    /// <param name="id">The tenant id</param>
    /// <param name="name">The tenant name</param>
    /// <param name="description">Optional description</param>
    /// <returns>A new Tenant instance</returns>
    public static Tenant CreateWithId(Guid id, string name, string? description = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Tenant id cannot be empty", nameof(id));

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tenant name cannot be empty", nameof(name));

        return new Tenant(id, name.Trim(), description?.Trim());
    }

    /// <summary>
    /// Updates the tenant information.
    /// </summary>
    /// <param name="name">The new tenant name</param>
    /// <param name="description">Optional new description</param>
    public void UpdateInfo(string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tenant name cannot be empty", nameof(name));

        Name = name.Trim();
        Description = description?.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Deactivates the tenant (soft delete).
    /// </summary>
    public void Deactivate()
    {
        IsActive = false;
        DeletedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Reactivates the tenant.
    /// </summary>
    public void Reactivate()
    {
        IsActive = true;
        DeletedAt = null;
        UpdatedAt = DateTime.UtcNow;
    }
}