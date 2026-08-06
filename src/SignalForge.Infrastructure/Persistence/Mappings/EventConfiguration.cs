using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalForge.Domain.Models;

namespace SignalForge.Infrastructure.Persistence.Mappings;

/// <summary>
/// Entity Framework Core configuration for the Event entity.
/// </summary>
public class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("Events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.ExternalEventId)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(e => e.EventType)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(e => e.OccurredAt)
            .IsRequired();

        builder.Property(e => e.Payload)
            .IsRequired();

        builder.Property(e => e.ReceivedAt)
            .IsRequired();

        builder.Property(e => e.ProcessedAt)
            .IsRequired(false);

        builder.Property(e => e.IsProcessed)
            .IsRequired();

        // Navigation properties
        builder.HasOne(e => e.Tenant)
            .WithMany(t => t.Events)
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.TenantId, e.ExternalEventId }).IsUnique();
        builder.HasMany(e => e.WorkflowExecutions)
            .WithOne(we => we.Event)
            .HasForeignKey(we => we.EventId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}