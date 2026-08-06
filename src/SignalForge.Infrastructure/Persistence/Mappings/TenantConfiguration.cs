using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalForge.Domain.Models;

namespace SignalForge.Infrastructure.Persistence.Mappings;

/// <summary>
/// Entity Framework Core configuration for the Tenant entity.
/// </summary>
public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(t => t.Description)
            .HasMaxLength(1000);

        builder.Property(t => t.IsActive)
            .IsRequired();

        builder.Property(t => t.CreatedAt)
            .IsRequired();

        builder.Property(t => t.UpdatedAt)
            .IsRequired(false);

        builder.Property(t => t.DeletedAt)
            .IsRequired(false);

        // Navigation properties
        builder.HasMany(t => t.ApiKeys)
            .WithOne(ak => ak.Tenant)
            .HasForeignKey(ak => ak.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.Workflows)
            .WithOne(w => w.Tenant)
            .HasForeignKey(w => w.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.Events)
            .WithOne(e => e.Tenant)
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}