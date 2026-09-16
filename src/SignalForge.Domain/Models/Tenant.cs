namespace SignalForge.Domain.Models;

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

    public static Tenant Create(string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tenant name cannot be empty", nameof(name));

        return new Tenant(Guid.NewGuid(), name.Trim(), description?.Trim());
    }

    public static Tenant CreateWithId(Guid id, string name, string? description = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Tenant id cannot be empty", nameof(id));

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tenant name cannot be empty", nameof(name));

        return new Tenant(id, name.Trim(), description?.Trim());
    }

    public void UpdateInfo(string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tenant name cannot be empty", nameof(name));

        Name = name.Trim();
        Description = description?.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
        DeletedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Reactivate()
    {
        IsActive = true;
        DeletedAt = null;
        UpdatedAt = DateTime.UtcNow;
    }
}