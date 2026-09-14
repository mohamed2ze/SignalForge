using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalForge.Domain.Models;

namespace SignalForge.Infrastructure.Persistence.Mappings;

/// <summary>
/// Entity Framework Core configuration for the OutboxMessage entity.
/// </summary>
public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");

        builder.HasKey(ob => ob.Id);

        builder.Property(ob => ob.Id).ValueGeneratedNever();

        builder.Property(ob => ob.Type)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(ob => ob.Payload)
            .IsRequired();

        builder.Property(ob => ob.CreatedAt)
            .IsRequired();

        builder.Property(ob => ob.ProcessedAt)
            .IsRequired(false);

        builder.Property(ob => ob.FailedAt)
            .IsRequired(false);

        builder.Property(ob => ob.ClaimedAt)
            .IsRequired(false);

        builder.Property(ob => ob.AttemptCount)
            .IsRequired();

        // ErrorMessage is nvarchar(max): failure text can exceed 2000 chars, and the fixed cap used to
        // throw "String or binary data would be truncated". Bound at write time via
        // StorageText.TruncateForStorage.
        builder.Property(ob => ob.ErrorMessage)
            .IsRequired(false);

        builder.Property(ob => ob.IsProcessed)
            .IsRequired();

        builder.Property(ob => ob.ReplaySourceDeadLetterId)
            .IsRequired(false);

        // Worker poll query: "oldest unprocessed messages whose retry gate has passed and whose claim
        // is free or expired". The composite index serves the IsProcessed + FailedAt/NextRetryAt +
        // ClaimedAt filters and the CreatedAt/Id ordering without a sort.
        builder.HasIndex(ob => new { ob.IsProcessed, ob.FailedAt, ob.NextRetryAt, ob.ClaimedAt, ob.CreatedAt, ob.Id })
            .HasDatabaseName("IX_OutboxMessages_Poll");

        // Single in-flight requeue lookups: "any unprocessed outbox message replayed from dead
        // letter X". No FK — the dead letter stays visible and deletable independently of a
        // replay in flight. Enforced as a FILTERED UNIQUE index over the "in flight" subset,
        // which makes the idempotency rule race-proof — two concurrent replays of the same dead
        // letter cannot both insert an unprocessed requeue — and keeps the index small (only rows
        // where IsProcessed = false participate, and each dead letter can have at most one of
        // those). Processed/exhausted requeues leave the index; a later re-replay is allowed.
        builder.HasIndex(ob => ob.ReplaySourceDeadLetterId)
            .HasDatabaseName("UX_OutboxMessages_ReplaySourceDeadLetterId_Unprocessed")
            .IsUnique()
            // SQL Server's unfiltered unique index treats multiple NULLs as duplicates, so the
            // filter must exclude both processed rows and the (overwhelmingly common) rows with no
            // replay source. Only "an unprocessed requeue exists for dead letter X" participates.
            .HasFilter("[ReplaySourceDeadLetterId] IS NOT NULL AND [IsProcessed] = CAST(0 AS bit)");

        // Navigation properties (denormalized for querying)
        // No foreign key for TenantId as it's denormalized for performance
    }
}