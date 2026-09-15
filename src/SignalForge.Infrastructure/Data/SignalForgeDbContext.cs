using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Broker;
using SignalForge.Application.Data;
using SignalForge.Application.RateLimiting;
using SignalForge.Domain;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Persistence.Mappings;

namespace SignalForge.Infrastructure.Data;

/// <summary>
/// Entity Framework Core DbContext for SignalForge.
/// Manages database connections and entity mappings.
/// </summary>
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
    public DbSet<BrokerMessage> BrokerMessages => Set<BrokerMessage>();
    public DbSet<RateLimitCounter> RateLimitCounters => Set<RateLimitCounter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply entity configurations from the Mappings assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TenantConfiguration).Assembly);

        // Soft delete is intentionally NOT enforced with global query filters: filtering the required
        // end (Tenant/Workflow) would silently drop children through INNER JOINs (EF 10622) — a
        // soft-deleted workflow's executions vanished from the observability list while the count
        // still included them. Deleted-at semantics are applied explicitly, per-query, where the
        // product requires them (management/execution surfaces), and historical execution/
        // dead-letter data intentionally survives workflow deletion.

        // Configure concurrency tokens (rowversion equivalent using UpdatedAt)
        ConfigureConcurrencyTokens(modelBuilder);
    }

    private static void ConfigureConcurrencyTokens(ModelBuilder modelBuilder)
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