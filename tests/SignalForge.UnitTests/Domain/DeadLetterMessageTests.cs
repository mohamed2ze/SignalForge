using System.Reflection;
using SignalForge.Domain.Models;

namespace SignalForge.UnitTests.Domain;

public class DeadLetterMessageTests
{
    private static void SetWorkflowStepNavigation(WorkflowStepExecution execution, WorkflowStep step)
    {
        typeof(WorkflowStepExecution)
            .GetProperty(nameof(WorkflowStepExecution.WorkflowStep))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(execution, new object[] { step });
    }

    [Fact]
    public void CreateFromOutboxMessage_copies_fields()
    {
        var tenantId = Guid.NewGuid();
        var outbox = OutboxMessage.Create(tenantId, "Test/Fail", """{"boom":true}""");
        outbox.IncrementAttempt();

        var deadLetter = DeadLetterMessage.CreateFromOutboxMessage(outbox, "final error");

        Assert.Equal(tenantId, deadLetter.TenantId);
        Assert.Equal("Test/Fail", deadLetter.OriginalMessageType);
        Assert.Equal("""{"boom":true}""", deadLetter.OriginalPayload);
        Assert.Equal("Outbox", deadLetter.FailedStepType);
        Assert.Equal(0, deadLetter.FailedStepNumber);
        Assert.Null(deadLetter.WorkflowExecutionId);
        Assert.Null(deadLetter.WorkflowStepExecutionId);
        Assert.Equal("final error", deadLetter.ErrorMessage);
        Assert.Equal(1, deadLetter.FinalAttemptCount);
        Assert.False(deadLetter.IsProcessed);
        Assert.NotEqual(default, deadLetter.CreatedAt);
    }

    [Fact]
    public void CreateFromFailedStep_links_executions()
    {
        var workflowId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var workflowExecution = WorkflowExecution.Create(workflowId, versionId, eventId, tenantId);

        var step = WorkflowStep.Create(Guid.NewGuid(), 3, "HttpWebhook", """{"url":"https://x"}""");
        var stepExecution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 3);
        SetWorkflowStepNavigation(stepExecution, step);
        stepExecution.Start();
        stepExecution.Fail("HTTP 500");

        var deadLetter = DeadLetterMessage.CreateFromFailedStep(stepExecution, workflowExecution);

        Assert.Equal(tenantId, deadLetter.TenantId);
        Assert.Equal("HttpWebhook", deadLetter.OriginalMessageType);
        Assert.Equal("{}", deadLetter.OriginalPayload);
        Assert.Equal(workflowExecution.Id, deadLetter.WorkflowExecutionId);
        Assert.Equal(stepExecution.Id, deadLetter.WorkflowStepExecutionId);
        Assert.Equal(3, deadLetter.FailedStepNumber);
        Assert.Equal("HTTP 500", deadLetter.ErrorMessage);
        Assert.Equal(1, deadLetter.FinalAttemptCount);
        Assert.False(deadLetter.IsProcessed);
    }

    [Fact]
    public void MarkAsProcessed_flips_state()
    {
        var outbox = OutboxMessage.Create(Guid.NewGuid(), "Test/Fail", "{}");
        var deadLetter = DeadLetterMessage.CreateFromOutboxMessage(outbox, "boom");

        deadLetter.MarkAsProcessed();

        Assert.True(deadLetter.IsProcessed);
        Assert.NotNull(deadLetter.ProcessedAt);
    }

    [Fact]
    public void CreateFromOutboxMessage_propagates_replay_provenance()
    {
        var sourceId = Guid.NewGuid();
        var outbox = OutboxMessage.CreateReplay(Guid.NewGuid(), "OrderCreated", "{}", sourceId);

        var deadLetter = DeadLetterMessage.CreateFromOutboxMessage(outbox, "replay re-failed");

        Assert.Equal(sourceId, deadLetter.ReplayedFromDeadLetterId);
        Assert.Equal(0, deadLetter.ReplayCount);
        Assert.Null(deadLetter.LastReplayedAt);
    }

    [Fact]
    public void CreateFromOutboxMessage_without_provenance_leaves_link_null()
    {
        var outbox = OutboxMessage.Create(Guid.NewGuid(), "OrderCreated", "{}");

        var deadLetter = DeadLetterMessage.CreateFromOutboxMessage(outbox, "boom");

        Assert.Null(deadLetter.ReplayedFromDeadLetterId);
        Assert.Equal(0, deadLetter.ReplayCount);
    }

    [Fact]
    public void RecordReplay_increments_and_stamps()
    {
        var outbox = OutboxMessage.Create(Guid.NewGuid(), "OrderCreated", "{}");
        var deadLetter = DeadLetterMessage.CreateFromOutboxMessage(outbox, "boom");

        deadLetter.RecordReplay();
        var first = deadLetter.LastReplayedAt;

        Assert.Equal(1, deadLetter.ReplayCount);
        Assert.NotNull(first);

        deadLetter.RecordReplay();

        Assert.Equal(2, deadLetter.ReplayCount);
        Assert.NotNull(deadLetter.LastReplayedAt);
        Assert.True(deadLetter.LastReplayedAt >= first);
    }
}