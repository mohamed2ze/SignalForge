using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="StepExecutionRetryPolicy"/>, the shared retry/backoff/dead-letter
/// decision extracted out of <see cref="WorkflowExecutionOrchestratorService"/>. Both the worker's
/// advancement catch and the HTTP manual-retry endpoint delegate to this service, so a step must
/// exhaust its attempts identically through any entry point.
/// </summary>
public class StepExecutionRetryPolicyTests
{
    private static SignalForgeDbContext CreateDb()
        => new(new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StepExecutionRetryPolicy CreatePolicy(SignalForgeDbContext db)
        => new(db, Options.Create(new StepRetryPolicyOptions()), NullLogger<StepExecutionRetryPolicy>.Instance);

    private static async Task<WorkflowExecution> SeedRunningExecutionAsync(SignalForgeDbContext db)
    {
        var tenantId = Guid.NewGuid();
        var workflow = Workflow.Create(tenantId, "retry-policy");
        var version = WorkflowVersion.Create(workflow.Id, 1);
        var @event = Event.Create(tenantId, "ext-1", "order.created", DateTime.UtcNow, "{}");

        db.Tenants.Add(Tenant.CreateWithId(tenantId, "retry-policy-test"));
        db.Workflows.Add(workflow);
        db.Events.Add(@event);

        var execution = WorkflowExecution.Create(workflow.Id, version.Id, @event.Id, tenantId);
        execution.Start();
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();
        return execution;
    }

    private static WorkflowStepExecution NewFailedStep(
        WorkflowExecution execution, int maxAttempts = 3)
    {
        var step = WorkflowStepExecution.Create(execution.Id, Guid.NewGuid(), 1, maxAttempts);
        step.Start();   // attempt 1
        step.Fail("simulated step failure");
        return step;
    }

    [Fact]
    public async Task ResolveFailure_WithAttemptsRemaining_SchedulesExponentialBackoffRetry()
    {
        var db = CreateDb();
        var execution = await SeedRunningExecutionAsync(db);
        var step = NewFailedStep(execution, maxAttempts: 3);
        db.WorkflowStepExecutions.Add(step);
        await db.SaveChangesAsync();

        var resolution = await CreatePolicy(db).ResolveFailureAsync(
            execution, step, nameof(StepType.Delay));

        Assert.Equal(StepFailureResolution.Retrying, resolution);
        Assert.Equal(WorkflowStepExecutionStatus.Retrying, step.Status);
        Assert.Equal(WorkflowExecutionStatus.Running, execution.Status);
        Assert.Empty(db.DeadLetterMessages);

        // Backoff = 2^attempt seconds jittered to ±50% (attempt 1 → base 2s, delay in [1s, 2s)).
        Assert.NotNull(step.NextRetryAt);
        var baseSeconds = Math.Min(Math.Pow(2, step.AttemptNumber), new StepRetryPolicyOptions().MaxBackoffSeconds);
        var delaySeconds = (step.NextRetryAt!.Value - DateTime.UtcNow).TotalSeconds;
        Assert.InRange(delaySeconds, baseSeconds * 0.5 - 1, baseSeconds + 1);
    }

    [Fact]
    public async Task ResolveFailure_WhenAttemptsExhausted_CreatesExactlyOneDeadLetterAndFailsExecution()
    {
        var db = CreateDb();
        var execution = await SeedRunningExecutionAsync(db);
        // MaxAttempts = 1: attempt 1 already consumed, so CanRetry is false.
        var step = NewFailedStep(execution, maxAttempts: 1);
        db.WorkflowStepExecutions.Add(step);
        await db.SaveChangesAsync();

        var resolution = await CreatePolicy(db).ResolveFailureAsync(
            execution, step, nameof(StepType.NotificationSimulation));

        Assert.Equal(StepFailureResolution.DeadLettered, resolution);
        Assert.Equal(WorkflowExecutionStatus.Failed, execution.Status);

        var deadLetter = await db.DeadLetterMessages.SingleAsync();
        Assert.Equal(execution.Id, deadLetter.WorkflowExecutionId);
        Assert.Equal(step.Id, deadLetter.WorkflowStepExecutionId);
        Assert.Equal(nameof(StepType.NotificationSimulation), deadLetter.FailedStepType);
        Assert.Equal(1, deadLetter.FinalAttemptCount);
    }

    [Fact]
    public async Task ScheduleRetry_AppliesExponentialBackoffFromCurrentAttempt()
    {
        var db = CreateDb();
        var execution = await SeedRunningExecutionAsync(db);
        var step = NewFailedStep(execution, maxAttempts: 5);
        db.WorkflowStepExecutions.Add(step);
        await db.SaveChangesAsync();

        await CreatePolicy(db).ScheduleRetryAsync(step);

        Assert.Equal(WorkflowStepExecutionStatus.Retrying, step.Status);
        Assert.NotNull(step.NextRetryAt);
        var baseSeconds = Math.Min(Math.Pow(2, step.AttemptNumber), new StepRetryPolicyOptions().MaxBackoffSeconds);
        var delaySeconds = (step.NextRetryAt!.Value - DateTime.UtcNow).TotalSeconds;
        Assert.InRange(delaySeconds, baseSeconds * 0.5 - 1, baseSeconds + 1);
    }

    [Fact]
    public async Task ScheduleRetry_CapsBackoffAtTheConfiguredMaximum()
    {
        var db = CreateDb();
        var execution = await SeedRunningExecutionAsync(db);
        var step = NewFailedStep(execution, maxAttempts: 20);
        db.WorkflowStepExecutions.Add(step);
        await db.SaveChangesAsync();

        var policy = new StepExecutionRetryPolicy(
            db,
            Options.Create(new StepRetryPolicyOptions { MaxBackoffSeconds = 30 }),
            NullLogger<StepExecutionRetryPolicy>.Instance);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            step.Retry(DateTime.UtcNow);
            await db.SaveChangesAsync();
            step.PrepareForRetry();
            await db.SaveChangesAsync();
            step.Start();
            await db.SaveChangesAsync();
            step.Fail("simulated step failure");
            await db.SaveChangesAsync();
        }

        await policy.ScheduleRetryAsync(step);

        var delaySeconds = (step.NextRetryAt!.Value - DateTime.UtcNow).TotalSeconds;
        Assert.InRange(delaySeconds, 15 - 1, 30 + 1); // capped at 30s, jittered to ±50%
    }
}