using SignalForge.Domain.Models;

namespace SignalForge.Api.Dtos;

/// <summary>
/// Response model for dead letter message data.
/// </summary>
public class DeadLetterDto
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string OriginalMessageType { get; set; } = default!;
    public string OriginalPayload { get; set; } = default!;
    public string FailedStepType { get; set; } = default!;
    public int FailedStepNumber { get; set; }
    public Guid? WorkflowExecutionId { get; set; }
    public Guid? WorkflowStepExecutionId { get; set; }
    public string ErrorMessage { get; set; } = default!;
    public int FinalAttemptCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public bool IsProcessed { get; set; }
    public int ReplayCount { get; set; }
    public DateTime? LastReplayedAt { get; set; }
    public Guid? ReplayedFromDeadLetterId { get; set; }

    public static DeadLetterDto FromDomain(DeadLetterMessage deadLetter)
    {
        return new DeadLetterDto
        {
            Id = deadLetter.Id,
            TenantId = deadLetter.TenantId,
            OriginalMessageType = deadLetter.OriginalMessageType,
            OriginalPayload = deadLetter.OriginalPayload,
            FailedStepType = deadLetter.FailedStepType,
            FailedStepNumber = deadLetter.FailedStepNumber,
            WorkflowExecutionId = deadLetter.WorkflowExecutionId,
            WorkflowStepExecutionId = deadLetter.WorkflowStepExecutionId,
            ErrorMessage = deadLetter.ErrorMessage,
            FinalAttemptCount = deadLetter.FinalAttemptCount,
            CreatedAt = deadLetter.CreatedAt,
            ProcessedAt = deadLetter.ProcessedAt,
            IsProcessed = deadLetter.IsProcessed,
            ReplayCount = deadLetter.ReplayCount,
            LastReplayedAt = deadLetter.LastReplayedAt,
            ReplayedFromDeadLetterId = deadLetter.ReplayedFromDeadLetterId
        };
    }
}

/// <summary>
/// Paged envelope for the Level 5 dead-letter list (Decision #25): items plus the applied
/// page/pageSize and the exact total matching the filters so clients can page without guessing.
/// </summary>
public class PagedDeadLettersDto
{
    public List<DeadLetterDto> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
}

/// <summary>
/// Response model for dead letter counts.
/// </summary>
public class DeadLetterCountsDto
{
    public int Total { get; set; }
    public int Unprocessed { get; set; }
    public int Processed { get; set; }
}