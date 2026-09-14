using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalForge.Domain.Models;

namespace SignalForge.Infrastructure.Persistence.Mappings;

/// <summary>
/// Entity Framework Core configuration for the ApiKey entity.
/// </summary>
public class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("ApiKeys");

        builder.HasKey(ak => ak.Id);

        builder.Property(ak => ak.Id).ValueGeneratedNever();

        builder.Property(ak => ak.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(ak => ak.KeyHash)
            .IsRequired()
            .HasMaxLength(512); // Versioned PBKDF2 hash ("$pbkdf2-sha256$..." ~85 chars) or legacy base64 SHA-256

        builder.Property(ak => ak.KeyPrefix)
            .HasMaxLength(20);

        builder.Property(ak => ak.IsActive)
            .IsRequired();

        builder.Property(ak => ak.CreatedAt)
            .IsRequired();

        builder.Property(ak => ak.ExpiresAt)
            .IsRequired(false);

        builder.Property(ak => ak.LastUsedAt)
            .IsRequired(false);

        builder.Property(ak => ak.RevokedAt)
            .IsRequired(false);

        // Navigation properties
        builder.HasOne(ak => ak.Tenant)
            .WithMany(t => t.ApiKeys)
            .HasForeignKey(ak => ak.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}