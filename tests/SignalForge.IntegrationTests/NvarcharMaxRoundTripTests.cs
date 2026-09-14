using Microsoft.EntityFrameworkCore;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.IntegrationTests;

/// <summary>
/// Regression pins for H2: ErrorMessage/Output columns widened from nvarchar(2000) to
/// nvarchar(max). Before the migration any text longer than 2000 characters failed to persist
/// with a data truncation error; these tests round-trip long strings through every affected table.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class NvarcharMaxRoundTripTests : ApiTestBase
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly string Key = $"c{Guid.NewGuid():N}NvMax";

    public NvarcharMaxRoundTripTests(MsSqlContainerFixture database)
        : base(database)
    {
    }

    [Fact]
    public async Task StepExecution_Output_And_ErrorMessage_RoundTrip_Beyond_2000_Chars()
    {
        using var ctx = new SignalForgeDbContext(DbOptions());
        await EnsureTenantAsync(ctx, TenantId, "itest-nvmax", Key);

        var workflow = Workflow.Create(TenantId, "nv-max-wf", "long-text regression workflow");
        var version = WorkflowVersion.Create(workflow.Id, 1);
        version.AddStep(1, nameof(StepType.HttpWebhook), """{"url":"https://example.com"}""", "hook");
        version.Publish();
        var evt = Event.Create(TenantId, $"nvmax-{Guid.NewGuid():N}", "order.created", DateTime.UtcNow, "{}");
        var execution = WorkflowExecution.Create(workflow.Id, version.Id, evt.Id, TenantId);
        execution.Start();

        var step = version.Steps.Single(s => s.StepNumber == 1);
        var failed = WorkflowStepExecution.Create(execution.Id, step.Id, 1);
        SetWorkflowStepNavigation(failed, step);
        failed.Start();
        var succeeded = WorkflowStepExecution.Create(execution.Id, step.Id, 1);
        SetWorkflowStepNavigation(succeeded, step);
        succeeded.Start();

        var longOutput = new string('x', 3000);
        var longError = new string('y', 3000) + $" suffix-{Guid.NewGuid()}";

        ctx.Workflows.Add(workflow);
        ctx.WorkflowVersions.Add(version);
        ctx.Events.Add(evt);
        ctx.WorkflowExecutions.Add(execution);
        ctx.WorkflowStepExecutions.Add(failed);
        ctx.WorkflowStepExecutions.Add(succeeded);
        await ctx.SaveChangesAsync();

        var reloaded = await ctx.WorkflowStepExecutions
            .Where(s => s.WorkflowExecutionId == execution.Id)
            .ToListAsync();
        Assert.Equal(2, reloaded.Count);
        reloaded.Single(s => s.Id == failed.Id).Fail(longError);
        reloaded.Single(s => s.Id == succeeded.Id).Succeed(longOutput);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var stored = await ctx.WorkflowStepExecutions
            .Where(s => s.WorkflowExecutionId == execution.Id)
            .ToListAsync();
        Assert.Equal(longError, stored.Single(s => s.Id == failed.Id).ErrorMessage);
        Assert.Equal(longOutput, stored.Single(s => s.Id == succeeded.Id).Output);

        await CleanupAsync(TenantId);
    }

    [Fact]
    public async Task Outbox_And_DeadLetter_ErrorMessage_RoundTrip_Beyond_2000_Chars()
    {
        using var ctx = new SignalForgeDbContext(DbOptions());
        await EnsureTenantAsync(ctx, TenantId, "itest-nvmax", Key);

        var longError = new string('z', 3000) + $" outbox-{Guid.NewGuid()}";

        var outbox = OutboxMessage.Create(TenantId, "order.updated", """{"id":7}""");
        outbox.IncrementAttempt();
        outbox.MarkAsFailed(longError);
        var deadLetter = DeadLetterMessage.CreateFromOutboxMessage(outbox, longError);

        ctx.OutboxMessages.Add(outbox);
        ctx.DeadLetterMessages.Add(deadLetter);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var storedOutbox = await ctx.OutboxMessages.SingleAsync(m => m.Id == outbox.Id);
        var storedDeadLetter = await ctx.DeadLetterMessages.SingleAsync(m => m.Id == deadLetter.Id);
        Assert.Equal(longError, storedOutbox.ErrorMessage);
        Assert.Equal(longError, storedDeadLetter.ErrorMessage);

        await CleanupAsync(TenantId);
    }
}