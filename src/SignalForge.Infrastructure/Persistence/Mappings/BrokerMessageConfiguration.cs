using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalForge.Application.Broker;

namespace SignalForge.Infrastructure.Persistence.Mappings;

/// <summary>
/// EF mapping for the durable broker message log. Messages are keyed by their generated ID and
/// append-only; <paramref name="PublishedAt"/> is indexed so audit reads stay in publication order.
/// </summary>
public sealed class BrokerMessageConfiguration : IEntityTypeConfiguration<BrokerMessage>
{
    public void Configure(EntityTypeBuilder<BrokerMessage> builder)
    {
        builder.ToTable("BrokerMessages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasMaxLength(40);
        builder.Property(m => m.Type).HasMaxLength(200).IsRequired();
        builder.Property(m => m.Payload).IsRequired();
        builder.Property(m => m.PublishedAt);
        builder.HasIndex(m => m.PublishedAt);
    }
}