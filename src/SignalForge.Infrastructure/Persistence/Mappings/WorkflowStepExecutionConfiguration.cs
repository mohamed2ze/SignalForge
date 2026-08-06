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

        builder.Property(wse => wse.ErrorMessage)
            .HasMaxLength(2000);

        builder.Property(wse => wse.Output)
            .HasMaxLength(2000);

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