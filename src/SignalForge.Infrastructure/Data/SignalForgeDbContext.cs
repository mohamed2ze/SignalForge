using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Data;
using SignalForge.Domain;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Persistence.Mappings;

namespace SignalForge.Infrastructure.Data;

/// <summary>
/// Entity Framework Core DbContext for SignalForge.
/// Manages database connections and entity mappings.
/// </>
public class SignalForgeDbContext : DbContext, ISignalForgeDbContext
{
    public SignalForgeDbContext(DbContextOptions<SignalForgeDbContext> options)
        : base(options)
    {
    }

    // DbSets for each entity
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();
    public DbSet<WorkflowStep> WorkflowSteps => Set<WorkflowStep>();
    public DbSet<WorkflowExecution> WorkflowExecutions => Set<WorkflowExecution>();
    public DbSet<WorkflowStepExecution> WorkflowStepExecutions => Set<WorkflowStepExecution>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<DeadLetterMessage> DeadLetterMessages => Set<DeadLetterMessage>();
    public DbSet<TenantWebhookSigningSetting> TenantWebhookSigningSettings => Set<TenantWebhookSigningSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply entity configurations from the Mappings assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TenantConfiguration).Assembly);

        // Configure global query filters for soft deletes
        modelBuilder.Entity<Tenant>()
            .HasQueryFilter(t => t.DeletedAt == null);

        modelBuilder.Entity<Workflow>()
            .HasQueryFilter(w => w.DeletedAt == null);

        // Configure concurrency tokens (rowversion equivalent using UpdatedAt)
        ConfigureConcurrencyTokens(modelBuilder);
    }

    private void ConfigureConcurrencyTokens(ModelBuilder modelBuilder)
    {
        // UpdatedAt doubles as an optimistic-concurrency token (rowversion-like behavior).
        // The value is ALWAYS set client-side by the domain (constructors/update methods), so it
        // must NOT be marked ValueGeneratedOnAddOrUpdate — that would make EF omit it from INSERTs
        // and fail on NOT NULL columns (SQL Server cannot auto-generate datetime2).
        modelBuilder.Entity<Tenant>()
            .Property(t => t.UpdatedAt)
            .IsConcurrencyToken();

        modelBuilder.Entity<ApiKey>()
            .Property(ak => ak.UpdatedAt)
            .IsConcurrencyToken();

        modelBuilder.Entity<Workflow>()
            .Property(w => w.UpdatedAt)
            .IsConcurrencyToken();

        modelBuilder.Entity<WorkflowVersion>()
            .Property(wv => wv.UpdatedAt)
            .IsConcurrencyToken();

        modelBuilder.Entity<WorkflowStep>()
            .Property(ws => ws.UpdatedAt)
            .IsConcurrencyToken();
    }
}