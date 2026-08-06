using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalForge.Domain.Models;

namespace SignalForge.Infrastructure.Persistence.Mappings;

/// <summary>
/// Entity Framework Core configuration for the WorkflowVersion entity.
/// </summary>
public class WorkflowVersionConfiguration : IEntityTypeConfiguration<WorkflowVersion>
{
    public void Configure(EntityTypeBuilder<WorkflowVersion> builder)
    {
        builder.ToTable("WorkflowVersions");

        builder.HasKey(wv => wv.Id);

        builder.Property(wv => wv.Id).ValueGeneratedNever();

        builder.Property(wv => wv.VersionNumber)
            .IsRequired();

        builder.Property(wv => wv.Description)
            .HasMaxLength(1000);

        builder.Property(wv => wv.IsPublished)
            .IsRequired();

        builder.Property(wv => wv.CreatedAt)
            .IsRequired();

        builder.Property(wv => wv.PublishedAt)
            .IsRequired(false);

        // Navigation properties
        builder.HasOne(wv => wv.Workflow)
            .WithMany(w => w.Versions)
            .HasForeignKey(wv => wv.WorkflowId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(wv => wv.Steps)
            .WithOne(ws => ws.WorkflowVersion)
            .HasForeignKey(ws => ws.WorkflowVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(wv => wv.Executions)
            .WithOne(we => we.WorkflowVersion)
            .HasForeignKey(we => we.WorkflowVersionId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}