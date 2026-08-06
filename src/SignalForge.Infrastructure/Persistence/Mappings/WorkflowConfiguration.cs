using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalForge.Domain.Models;

namespace SignalForge.Infrastructure.Persistence.Mappings;

/// <summary>
/// Entity Framework Core configuration for the Workflow entity.
/// </summary>
public class WorkflowConfiguration : IEntityTypeConfiguration<Workflow>
{
    public void Configure(EntityTypeBuilder<Workflow> builder)
    {
        builder.ToTable("Workflows");

        builder.HasKey(w => w.Id);

        builder.Property(w => w.Id).ValueGeneratedNever();

        builder.Property(w => w.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(w => w.Description)
            .HasMaxLength(1000);

        builder.Property(w => w.IsEnabled)
            .IsRequired();

        builder.Property(w => w.CreatedAt)
            .IsRequired();

        builder.Property(w => w.UpdatedAt)
            .IsRequired(false);

        builder.Property(w => w.DeletedAt)
            .IsRequired(false);

        // Navigation properties
        builder.HasOne(w => w.Tenant)
            .WithMany(t => t.Workflows)
            .HasForeignKey(w => w.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(w => w.Versions)
            .WithOne(wv => wv.Workflow)
            .HasForeignKey(wv => wv.WorkflowId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}