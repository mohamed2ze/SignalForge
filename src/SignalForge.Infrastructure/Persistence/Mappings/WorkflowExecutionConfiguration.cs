using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalForge.Domain.Models;

namespace SignalForge.Infrastructure.Persistence.Mappings;

/// <summary>
/// Entity Framework Core configuration for the WorkflowExecution entity.
/// </summary>
public class WorkflowExecutionConfiguration : IEntityTypeConfiguration<WorkflowExecution>
{
    public void Configure(EntityTypeBuilder<WorkflowExecution> builder)
    {
        builder.ToTable("WorkflowExecutions");

        builder.HasKey(we => we.Id);

        builder.Property(we => we.Id).ValueGeneratedNever();

        builder.Property(we => we.Status)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(we => we.CurrentStepNumber)
            .IsRequired();

        builder.Property(we => we.StartedAt)
            .IsRequired();

        builder.Property(we => we.CompletedAt)
            .IsRequired(false);

        builder.Property(we => we.RetryCount)
            .IsRequired();

        builder.Property(we => we.ErrorMessage)
            .HasMaxLength(2000);

        // Navigation properties
        builder.HasOne(we => we.Workflow)
            .WithMany(w => w.Executions)
            .HasForeignKey(we => we.WorkflowId)
            .OnDelete(DeleteBehavior.NoAction); // Prevent multiple cascade paths

        builder.HasOne(we => we.WorkflowVersion)
            .WithMany(wv => wv.Executions)
            .HasForeignKey(we => we.WorkflowVersionId)
            .OnDelete(DeleteBehavior.NoAction); // Prevent multiple cascade paths

        builder.HasOne(we => we.Event)
            .WithMany(e => e.WorkflowExecutions)
            .HasForeignKey(we => we.EventId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(we => we.StepExecutions)
            .WithOne(wse => wse.WorkflowExecution)
            .HasForeignKey(wse => wse.WorkflowExecutionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}