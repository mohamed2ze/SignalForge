using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalForge.Domain.Models;

namespace SignalForge.Infrastructure.Persistence.Mappings;

/// <summary>
/// Entity Framework Core configuration for the WorkflowStepExecution entity.
/// </summary>
public class WorkflowStepExecutionConfiguration : IEntityTypeConfiguration<WorkflowStepExecution>
{
    public void Configure(EntityTypeBuilder<WorkflowStepExecution> builder)
    {
        builder.ToTable("WorkflowStepExecutions");

        builder.HasKey(wse => wse.Id);

        builder.Property(wse => wse.Id).ValueGeneratedNever();

        builder.Property(wse => wse.StepNumber)
            .IsRequired();

        builder.Property(wse => wse.Status)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(wse => wse.AttemptNumber)
            .IsRequired();

        builder.Property(wse => wse.MaxAttempts)
            .IsRequired();

        builder.Property(wse => wse.StartedAt)
            .IsRequired();

        builder.Property(wse => wse.CompletedAt)
            .IsRequired(false);

        builder.Property(wse => wse.NextRetryAt)
            .IsRequired(false);

        // Error/Output are nvarchar(max): webhook responses and error text can legitimately exceed
        // 2000 chars, and a fixed cap used to throw "String or binary data would be truncated".
        // Write sites bound the size via StorageText.TruncateForStorage before persisting.
        builder.Property(wse => wse.ErrorMessage)
            .IsRequired(false);

        builder.Property(wse => wse.Output)
            .IsRequired(false);

        // Worker pump subquery looks up step executions by (execution, next step number) and
        // filters on status to decide whether the next step is already in flight.
        builder.HasIndex(wse => new { wse.WorkflowExecutionId, wse.StepNumber, wse.Status })
            .HasDatabaseName("IX_WorkflowStepExecutions_Execution_Step_Status");

        // Navigation properties
        builder.HasOne(wse => wse.WorkflowExecution)
            .WithMany(we => we.StepExecutions)
            .HasForeignKey(wse => wse.WorkflowExecutionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(wse => wse.WorkflowStep)
            .WithMany(ws => ws.StepExecutions)
            .HasForeignKey(wse => wse.WorkflowStepId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}