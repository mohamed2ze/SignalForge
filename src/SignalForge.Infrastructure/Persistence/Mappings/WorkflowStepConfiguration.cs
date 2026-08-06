using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalForge.Domain.Models;

namespace SignalForge.Infrastructure.Persistence.Mappings;

/// <summary>
/// Entity Framework Core configuration for the WorkflowStep entity.
/// </summary>
public class WorkflowStepConfiguration : IEntityTypeConfiguration<WorkflowStep>
{
    public void Configure(EntityTypeBuilder<WorkflowStep> builder)
    {
        builder.ToTable("WorkflowSteps");

        builder.HasKey(ws => ws.Id);

        builder.Property(ws => ws.Id).ValueGeneratedNever();

        builder.Property(ws => ws.StepNumber)
            .IsRequired();

        builder.Property(ws => ws.StepType)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(ws => ws.Name)
            .HasMaxLength(200);

        builder.Property(ws => ws.Description)
            .HasMaxLength(1000);

        builder.Property(ws => ws.Configuration)
            .IsRequired();

        builder.Property(ws => ws.IsEnabled)
            .IsRequired();

        builder.Property(ws => ws.CreatedAt)
            .IsRequired();

        // Navigation properties
        builder.HasOne(ws => ws.WorkflowVersion)
            .WithMany(wv => wv.Steps)
            .HasForeignKey(ws => ws.WorkflowVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(ws => ws.StepExecutions)
            .WithOne(wse => wse.WorkflowStep)
            .HasForeignKey(wse => wse.WorkflowStepId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}