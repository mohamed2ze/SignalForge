using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalForge.Domain.Models;

namespace SignalForge.Infrastructure.Persistence.Mappings;

/// <summary>
/// Entity Framework Core configuration for the TenantWebhookSigningSetting entity.
/// One row per tenant (TenantId is the primary key); no FK to Tenants is enforced so seeding
/// order stays flexible and tenant deletion (currently unused) cannot cascade unexpectedly.
/// </summary>
public class TenantWebhookSigningSettingConfiguration : IEntityTypeConfiguration<TenantWebhookSigningSetting>
{
    public void Configure(EntityTypeBuilder<TenantWebhookSigningSetting> builder)
    {
        builder.ToTable("TenantWebhookSigningSettings");

        builder.HasKey(t => t.TenantId);
        builder.Property(t => t.TenantId).ValueGeneratedNever();
        builder.Property(t => t.SigningSecret).HasMaxLength(256).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();
    }
}