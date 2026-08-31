using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SignalForge.Domain.Models;

namespace SignalForge.Infrastructure.Persistence.Mappings;

/// <summary>
/// Entity Framework Core configuration for the DeadLetterMessage entity.
/// </summary>
public class DeadLetterMessageConfiguration : IEntityTypeConfiguration<DeadLetterMessage>
{
    public void Configure(EntityTypeBuilder<DeadLetterMessage> builder)
    {
        builder.ToTable("DeadLetterMessages");

        builder.HasKey(dlm => dlm.Id);

        builder.Property(dlm => dlm.Id).ValueGeneratedNever();

        builder.Property(dlm => dlm.OriginalMessageType)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(dlm => dlm.OriginalPayload)
            .IsRequired();

        builder.Property(dlm => dlm.FailedStepType)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(dlm => dlm.ErrorMessage)
            .IsRequired()
            .HasMaxLength(2000);

        builder.Property(dlm => dlm.FinalAttemptCount)
            .IsRequired();

        builder.Property(dlm => dlm.CreatedAt)
            .IsRequired();

        builder.Property(dlm => dlm.ProcessedAt)
            .IsRequired(false);

        builder.Property(dlm => dlm.IsProcessed)
            .IsRequired();

        builder.Property(dlm => dlm.ReplayCount)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(dlm => dlm.LastReplayedAt)
            .IsRequired(false);

        builder.Property(dlm => dlm.ReplayedFromDeadLetterId)
            .IsRequired(false);

        // Navigation properties (denormalized for querying)
        // WorkflowExecution/WorkflowStepExecution are optional references: dead letters from the
        // outbox pipeline are not tied to a step execution, so the FK columns are nullable.
        // ClientNoAction keeps EF fix-up working without emitting a cascade FK, which SQL Server
        // would reject (multiple cascade paths via WorkflowExecutions).
        builder.HasOne(dlm => dlm.WorkflowExecution)
            .WithMany()
            .HasForeignKey(dlm => dlm.WorkflowExecutionId)
            .OnDelete(DeleteBehavior.ClientNoAction);

        builder.HasOne(dlm => dlm.WorkflowStepExecution)
            .WithMany()
            .HasForeignKey(dlm => dlm.WorkflowStepExecutionId)
            .OnDelete(DeleteBehavior.ClientNoAction);

        builder.HasIndex(dlm => dlm.TenantId);
        builder.HasIndex(dlm => dlm.WorkflowExecutionId);
        builder.HasIndex(dlm => dlm.CreatedAt);
        builder.HasIndex(dlm => dlm.ReplayedFromDeadLetterId);
    }
}