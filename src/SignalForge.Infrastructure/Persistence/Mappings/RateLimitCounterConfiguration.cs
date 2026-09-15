using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalForge.Application.RateLimiting;

namespace SignalForge.Infrastructure.Persistence.Mappings;

/// <summary>Maps <see cref="RateLimitCounter"/> to the <c>RateLimitCounters</c> table.</summary>
public sealed class RateLimitCounterConfiguration : IEntityTypeConfiguration<RateLimitCounter>
{
    public void Configure(EntityTypeBuilder<RateLimitCounter> builder)
    {
        builder.ToTable("RateLimitCounters");
        builder.HasKey(r => new { r.PartitionKey, r.WindowKey });
        builder.Property(r => r.PartitionKey).HasMaxLength(64).IsRequired();
        builder.Property(r => r.WindowKey).IsRequired();
        builder.Property(r => r.Count).IsRequired();
    }
}