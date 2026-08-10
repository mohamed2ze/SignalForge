using Microsoft.EntityFrameworkCore;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Data;

/// <summary>
/// Interface for the SignalForge DbContext to break circular dependency between Application and Infrastructure.
/// </summary>
public interface ISignalForgeDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<ApiKey> ApiKeys { get; }
    DbSet<Event> Events { get; }
    DbSet<Workflow> Workflows { get; }
    DbSet<WorkflowVersion> WorkflowVersions { get; }
    DbSet<WorkflowStep> WorkflowSteps { get; }
    DbSet<WorkflowExecution> WorkflowExecutions { get; }
    DbSet<WorkflowStepExecution> WorkflowStepExecutions { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<DeadLetterMessage> DeadLetterMessages { get; }
    DbSet<TenantWebhookSigningSetting> TenantWebhookSigningSettings { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}