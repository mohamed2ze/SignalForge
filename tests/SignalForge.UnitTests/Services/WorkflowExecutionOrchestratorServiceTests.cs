using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalForge.Application.Notifications;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.UnitTests.Services;

public class WorkflowExecutionOrchestratorServiceTests
{
    private static SignalForgeDbContext CreateDb()
        => new(new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IStepProcessorRegistry BuildProcessorRegistry()
    {
        var delay = new DelayStepProcessor(NullLogger<DelayStepProcessor>.Instance);
        var conditional = new ConditionalStepProcessor(NullLogger<ConditionalStepProcessor>.Instance);
        var logAudit = new LogAuditStepProcessor(NullLogger<LogAuditStepProcessor>.Instance);

        // NotificationStepProcessor + providers: needed for the retry re-entry tests, where a
        // notification step with no "recipient" throws KeyNotFoundException deterministically.
        var registry = new NotificationProviderRegistry(
        [
            new EmailNotificationProvider(new RecordingEmailTransport()),
            new SmsNotificationProvider(new RecordingSmsTransport())
        ]);
        var notification = new NotificationStepProcessor(
            registry,
            NullLogger<NotificationStepProcessor>.Instance);

        return new StepProcessorRegistry(
        [
            delay, conditional, logAudit, notification
        ]);
    }

    /// <summary>
    /// Builds the decomposable orchestrator graph (orchestrator + advancer + shared retry policy)
    /// over one <see cref="SignalForgeDbContext"/>, exactly as DI wires them in production.
    /// </summary>
    private static WorkflowExecutionOrchestratorService CreateOrchestrator(SignalForgeDbContext db)
    {
        var retryPolicy = new StepExecutionRetryPolicy(
            db,
            Options.Create(new StepRetryPolicyOptions()),
            NullLogger<StepExecutionRetryPolicy>.Instance);
        var advancer = new WorkflowExecutionAdvancer(
            db, BuildProcessorRegistry(), retryPolicy,
            NullLogger<WorkflowExecutionAdvancer>.Instance);
        return new WorkflowExecutionOrchestratorService(
            db, advancer, retryPolicy,
            NullLogger<WorkflowExecutionOrchestratorService>.Instance);
    }

    private static async Task<(Guid WorkflowId, Guid VersionId, Guid EventId, Guid TenantId)> SeedAsync(
        SignalForgeDbContext db, Action<WorkflowVersion> configure, bool publish = true,
        string eventType = "order.created", string eventPayload = "{}")
    {
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(Tenant.CreateWithId(tenantId, "orchestrator-test"));

        var workflow = Workflow.Create(tenantId, "orchestrator-wf");
        var version = workflow.CreateDraftVersion(1);
        configure(version);
        workflow.Enable();
        if (publish)
            version.Publish();

        var @event = Event.Create(tenantId, $"ext-{tenantId:N}", eventType, DateTime.UtcNow, eventPayload);
        db.Workflows.Add(workflow);
        db.Events.Add(@event);
        await db.SaveChangesAsync();

        return (workflow.Id, version.Id, @event.Id, tenantId);
    }

    [Fact]
    public async Task Sequential_chain_advances_until_completion()
    {
        var db = CreateDb();
        var (workflowId, versionId, eventId, tenantId) = await SeedAsync(db, version =>
        {
            version.AddStep(1, nameof(StepType.Delay), """{"seconds":0}""", "gap-1");
            version.AddStep(2, nameof(StepType.Delay), """{"seconds":0}""", "gap-2");
        });
        var orchestrator = CreateOrchestrator(db);

        var execution = await orchestrator.StartWorkflowExecutionAsync(workflowId, versionId, eventId, tenantId);

        Assert.Equal(WorkflowExecutionStatus.Running, execution.Status);
        Assert.Equal(0, execution.CurrentStepNumber);
        Assert.True(execution.IsRunning());

        Assert.True(await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id));
        Assert.Equal(1, execution.CurrentStepNumber);

        Assert.True(await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id));
        Assert.Equal(2, execution.CurrentStepNumber);
        Assert.Equal(2, execution.StepExecutions.Count);

        Assert.True(await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id));
        Assert.Equal(WorkflowExecutionStatus.Succeeded, execution.Status);
        Assert.True(execution.IsCompleted());
    }

    [Fact]
    public async Task Conditional_true_branch_routes_to_trueStep()
    {
        var db = CreateDb();
        var (workflowId, versionId, eventId, tenantId) = await SeedAsync(db, version =>
        {
            version.AddStep(1, nameof(StepType.Conditional),
                """{"expression":"$.event.type == 'order.created'","trueStep":4,"falseStep":5}""");
        });
        var orchestrator = CreateOrchestrator(db);

        var execution = await orchestrator.StartWorkflowExecutionAsync(workflowId, versionId, eventId, tenantId);

        Assert.True(await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id));

        var stepExecution = execution.StepExecutions.Single();
        Assert.Equal(WorkflowStepExecutionStatus.Succeeded, stepExecution.Status);
        Assert.Equal(4, stepExecution.RouteToStepNumber);
        Assert.Equal(4, execution.CurrentStepNumber);
    }

    [Fact]
    public async Task Conditional_false_branch_routes_to_falseStep()
    {
        var db = CreateDb();
        var (workflowId, versionId, eventId, tenantId) = await SeedAsync(
            db,
            version =>
            {
                version.AddStep(1, nameof(StepType.Conditional),
                    """{"expression":"$.event.type == 'order.created'","trueStep":4,"falseStep":5}""");
            },
            eventType: "order.cancelled");
        var orchestrator = CreateOrchestrator(db);

        var execution = await orchestrator.StartWorkflowExecutionAsync(workflowId, versionId, eventId, tenantId);

        Assert.True(await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id));

        Assert.Equal(5, execution.StepExecutions.Single().RouteToStepNumber);
        Assert.Equal(5, execution.CurrentStepNumber);
    }

    [Fact]
    public async Task Sequential_advance_when_no_route_target()
    {
        var db = CreateDb();
        var (workflowId, versionId, eventId, tenantId) = await SeedAsync(db, version =>
        {
            version.AddStep(1, nameof(StepType.LogAudit), """{"message":"hi","logLevel":"information"}""");
        });
        var orchestrator = CreateOrchestrator(db);

        var execution = await orchestrator.StartWorkflowExecutionAsync(workflowId, versionId, eventId, tenantId);

        Assert.True(await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id));

        Assert.Null(execution.StepExecutions.Single().RouteToStepNumber);
        Assert.Equal(1, execution.CurrentStepNumber);

        Assert.True(await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id));
        Assert.Equal(WorkflowExecutionStatus.Succeeded, execution.Status);
    }

    [Fact]
    public async Task Context_root_exposes_event_and_outputs_by_step_number()
    {
        var db = CreateDb();
        var (workflowId, versionId, eventId, tenantId) = await SeedAsync(
            db,
            version =>
            {
                version.AddStep(1, nameof(StepType.LogAudit), """{"message":"hi","logLevel":"information"}""");
                version.AddStep(2, nameof(StepType.Conditional),
                    """{"expression":"$.event.type == 'order.created' and $.event.payload.orderId == 'ORD-1' and $.output.1 == 'Audit logged: hi'","trueStep":9,"falseStep":2}""");
            },
            eventPayload: """{"orderId":"ORD-1"}""");
        var orchestrator = CreateOrchestrator(db);

        var execution = await orchestrator.StartWorkflowExecutionAsync(workflowId, versionId, eventId, tenantId);

        Assert.True(await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id));
        Assert.True(await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id));

        var conditional = execution.StepExecutions
            .Single(se => se.StepNumber == 2);
        Assert.Equal(WorkflowStepExecutionStatus.Succeeded, conditional.Status);
        Assert.Equal(9, conditional.RouteToStepNumber);
        Assert.Equal(9, execution.CurrentStepNumber);
    }

    [Fact]
    public async Task Execution_is_not_visible_to_other_tenants()
    {
        var db = CreateDb();
        var (workflowId, versionId, eventId, tenantId) = await SeedAsync(db, version =>
        {
            version.AddStep(1, nameof(StepType.Delay), """{"seconds":0}""");
        });

        var orchestrator = CreateOrchestrator(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.StartWorkflowExecutionAsync(workflowId, versionId, eventId, Guid.NewGuid()));

        var execution = await orchestrator.StartWorkflowExecutionAsync(
            workflowId, versionId, eventId, tenantId);
        Assert.Null(await orchestrator.GetWorkflowExecutionByIdAsync(execution.Id, Guid.NewGuid()));
        Assert.NotNull(await orchestrator.GetWorkflowExecutionByIdAsync(execution.Id, tenantId));
    }

    [Fact]
    public async Task Draft_versions_cannot_be_executed()
    {
        var db = CreateDb();
        var (workflowId, versionId, eventId, tenantId) = await SeedAsync(
            db, version => version.AddStep(1, nameof(StepType.Delay), """{"seconds":0}"""), publish: false);
        var orchestrator = CreateOrchestrator(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.StartWorkflowExecutionAsync(workflowId, versionId, eventId, tenantId));

        Assert.Contains("not published", ex.Message);
    }

    // ---------- retry re-entry: the pump drives in-place retries, no duplicate records ----------

    [Fact]
    public async Task Failing_step_is_retried_in_place_when_backoff_is_due()
    {
        // A notification step without "recipient" throws KeyNotFoundException on EVERY attempt,
        // giving a deterministic failing step.
        var db = CreateDb();
        var (workflowId, versionId, eventId, tenantId) = await SeedAsync(db, version =>
        {
            version.AddStep(1, nameof(StepType.NotificationSimulation), """{"type":"email"}""");
        });
        var orchestrator = CreateOrchestrator(db);

        var execution = await orchestrator.StartWorkflowExecutionAsync(workflowId, versionId, eventId, tenantId);

        // Attempt 1: step fails, engine schedules a retry (Retrying + backoff).
        Assert.True(await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id));

        var step = execution.StepExecutions.Single();
        Assert.Equal(WorkflowStepExecutionStatus.Retrying, step.Status);
        Assert.Equal(1, step.AttemptNumber);
        Assert.True(step.NextRetryAt > DateTime.UtcNow);

        // Attempt 2 (not due): busy -> false, still one record, nothing re-created.
        Assert.False(await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id));
        Assert.Single(execution.StepExecutions);
        Assert.Equal(WorkflowStepExecutionStatus.Retrying, step.Status);

        // Force the retry window to open, then re-enter: the SAME record is reused (no duplicate).
        db.Entry(step).Property(s => s.NextRetryAt).CurrentValue = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();

        Assert.True(await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id));
        Assert.Single(execution.StepExecutions);
        Assert.Equal(2, step.AttemptNumber);
        Assert.Equal(WorkflowStepExecutionStatus.Retrying, step.Status);
    }

    [Fact]
    public async Task Failing_step_exhausts_attempts_then_fails_the_execution()
    {
        var db = CreateDb();
        var (workflowId, versionId, eventId, tenantId) = await SeedAsync(db, version =>
        {
            version.AddStep(1, nameof(StepType.NotificationSimulation), """{"type":"email"}""");
        });
        var orchestrator = CreateOrchestrator(db);

        var execution = await orchestrator.StartWorkflowExecutionAsync(workflowId, versionId, eventId, tenantId);

        // Attempt 1 creates the record and (after the deterministic throw) schedules a retry.
        await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id);
        var step = execution.StepExecutions.Single();
        Assert.Equal(1, step.AttemptNumber);

        // Attempts 2..3: force-open the retry window before each re-entry. The engine re-enters
        // the SAME record, increments attempts, and on the last attempt fails the execution.
        for (var attempt = 2; attempt <= 3; attempt++)
        {
            db.Entry(step).Property(s => s.NextRetryAt).CurrentValue = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();

            await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id);
        }

        Assert.Equal(3, step!.AttemptNumber);
        Assert.Single(execution.StepExecutions); // reused record, never duplicated
        Assert.Equal(WorkflowStepExecutionStatus.Failed, step.Status);
        Assert.Equal(WorkflowExecutionStatus.Failed, execution.Status);
        Assert.True(execution.IsCompleted());
    }

    [Fact]
    public async Task Failing_step_exhausting_attempts_creates_exactly_one_dead_letter_and_fails_execution()
    {
        var db = CreateDb();
        var (workflowId, versionId, eventId, tenantId) = await SeedAsync(db, version =>
        {
            version.AddStep(1, nameof(StepType.NotificationSimulation), """{"type":"email"}""");
        });
        var orchestrator = CreateOrchestrator(db);

        var execution = await orchestrator.StartWorkflowExecutionAsync(workflowId, versionId, eventId, tenantId);

        // Attempt 1 creates the record and (after the deterministic throw) schedules a retry.
        await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id);
        var step = execution.StepExecutions.Single();

        // Attempts 2..3: force-open the retry window before each re-entry.
        for (var attempt = 2; attempt <= 3; attempt++)
        {
            db.Entry(step).Property(s => s.NextRetryAt).CurrentValue = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
            await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id);
        }

        // Exhausted: the shared retry/dead-letter path produced exactly one dead letter and a
        // Failed execution, and the dead letter points back at this step execution.
        var deadLetter = await db.DeadLetterMessages.SingleAsync();
        Assert.Equal(WorkflowExecutionStatus.Failed, execution.Status);
        Assert.Equal(deadLetter.WorkflowExecutionId, execution.Id);
        Assert.Equal(deadLetter.WorkflowStepExecutionId, step.Id);
        Assert.Equal(nameof(StepType.NotificationSimulation), deadLetter.FailedStepType);
        Assert.Equal(3, deadLetter.FinalAttemptCount);
    }

    [Fact]
    public async Task Manual_retry_schedules_the_shared_backoff_and_is_tenant_scoped()
    {
        var db = CreateDb();
        var (workflowId, versionId, eventId, tenantId) = await SeedAsync(db, version =>
        {
            version.AddStep(1, nameof(StepType.Delay), """{"seconds":0}""", "step-1");
        });
        var orchestrator = CreateOrchestrator(db);

        var execution = await orchestrator.StartWorkflowExecutionAsync(workflowId, versionId, eventId, tenantId);

        // A failed step execution with attempts remaining, as the worker would leave one behind.
        var step = await db.WorkflowSteps.SingleAsync(s => s.WorkflowVersionId == versionId && s.StepNumber == 1);
        var stepExecution = WorkflowStepExecution.Create(execution.Id, step.Id, 1);
        stepExecution.Start();
        stepExecution.Fail("simulated failure");
        db.WorkflowStepExecutions.Add(stepExecution);
        await db.SaveChangesAsync();

        // The shared exponential backoff (2^attempt) is applied, all in one tenant-scoped operation.
        var scheduled = await orchestrator.ScheduleStepRetryAsync(execution.Id, stepExecution.Id, tenantId);
        Assert.Equal(StepRetryStatus.Scheduled, scheduled.Status);
        Assert.Equal(stepExecution.Id, scheduled.StepExecution!.Id);
        Assert.Equal(WorkflowStepExecutionStatus.Retrying, scheduled.StepExecution.Status);
        Assert.NotNull(scheduled.StepExecution.NextRetryAt);
        Assert.True(scheduled.StepExecution.NextRetryAt > DateTime.UtcNow);

        // A retrying step whose window hasn't come due is not re-booked.
        var again = await orchestrator.ScheduleStepRetryAsync(execution.Id, stepExecution.Id, tenantId);
        Assert.Equal(StepRetryStatus.NotEligible, again.Status);

        // Another tenant cannot reach the execution (404-equivalent at the service boundary).
        var foreign = await orchestrator.ScheduleStepRetryAsync(execution.Id, stepExecution.Id, Guid.NewGuid());
        Assert.Equal(StepRetryStatus.NotFound, foreign.Status);

        // Unknown step execution under a real tenant is likewise not found, not scheduled.
        var missing = await orchestrator.ScheduleStepRetryAsync(execution.Id, Guid.NewGuid(), tenantId);
        Assert.Equal(StepRetryStatus.NotFound, missing.Status);
    }

    [Fact]
    public async Task Advancer_onto_an_already_completed_step_dead_letters_and_fails_execution()
    {
        var db = CreateDb();
        var (workflowId, versionId, eventId, tenantId) = await SeedAsync(db, version =>
        {
            version.AddStep(1, nameof(StepType.LogAudit), """{"message":"one","logLevel":"information"}""");
            version.AddStep(2, nameof(StepType.LogAudit), """{"message":"two","logLevel":"information"}""");
            version.AddStep(3, nameof(StepType.LogAudit), """{"message":"three","logLevel":"information"}""");
        });
        var orchestrator = CreateOrchestrator(db);

        var execution = await orchestrator.StartWorkflowExecutionAsync(workflowId, versionId, eventId, tenantId);

        // A conditional branch routed the execution back onto a step whose NEXT step already ran
        // to completion: step 3's record is Succeeded and CurrentStepNumber points at step 2.
        var step3 = await db.WorkflowSteps.SingleAsync(s => s.WorkflowVersionId == versionId && s.StepNumber == 3);
        var completedStep = WorkflowStepExecution.Create(execution.Id, step3.Id, 3);
        completedStep.Start();
        completedStep.Succeed();
        execution.StepExecutions.Add(completedStep);
        execution.RouteToStep(2);
        await db.SaveChangesAsync();

        // Re-entry onto the completed step must dead-letter once and fail the execution — never
        // throw out of PrepareForRetry (the old poison loop) nor hang the pump.
        Assert.True(await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id));

        Assert.Equal(WorkflowExecutionStatus.Failed, execution.Status);
        var deadLetter = await db.DeadLetterMessages.SingleAsync();
        Assert.Equal(completedStep.Id, deadLetter.WorkflowStepExecutionId);
        Assert.Equal(execution.Id, deadLetter.WorkflowExecutionId);
        Assert.Equal(nameof(StepType.LogAudit), deadLetter.FailedStepType);

        // A second advance over the now-completed execution is a no-op, not another dead letter.
        Assert.False(await orchestrator.AdvanceWorkflowExecutionAsync(execution.Id));
        Assert.Equal(1, await db.DeadLetterMessages.CountAsync());
    }

    [Fact]
    public async Task Concurrent_retry_scheduling_conflict_returns_not_eligible()
    {
        var db = CreateDb();
        var (workflowId, versionId, eventId, tenantId) = await SeedAsync(db, version =>
        {
            version.AddStep(1, nameof(StepType.Delay), """{"seconds":0}""", "step-1");
        });

        // Real components everywhere except the retry policy, which simulates the worker having
        // won the race (its committed schedule makes ours stale).
        var realPolicy = new StepExecutionRetryPolicy(
            db, Options.Create(new StepRetryPolicyOptions()), NullLogger<StepExecutionRetryPolicy>.Instance);
        var advancer = new WorkflowExecutionAdvancer(
            db, BuildProcessorRegistry(), realPolicy, NullLogger<WorkflowExecutionAdvancer>.Instance);
        var orchestrator = new WorkflowExecutionOrchestratorService(
            db, advancer, new ConcurrencyConflictRetryPolicy(), NullLogger<WorkflowExecutionOrchestratorService>.Instance);

        var execution = await orchestrator.StartWorkflowExecutionAsync(workflowId, versionId, eventId, tenantId);
        var step = await db.WorkflowSteps.SingleAsync(s => s.WorkflowVersionId == versionId && s.StepNumber == 1);
        var stepExecution = WorkflowStepExecution.Create(execution.Id, step.Id, 1);
        stepExecution.Start();
        stepExecution.Fail("simulated failure");
        db.WorkflowStepExecutions.Add(stepExecution);
        await db.SaveChangesAsync();

        var result = await orchestrator.ScheduleStepRetryAsync(execution.Id, stepExecution.Id, tenantId);

        // A DbUpdateConcurrencyException from the token check surfaces as NotEligible (HTTP 409),
        // never a 500, and the step is left untouched for the worker's commit.
        Assert.Equal(StepRetryStatus.NotEligible, result.Status);
        Assert.Equal(WorkflowStepExecutionStatus.Failed, stepExecution.Status);
    }

    /// <summary>
    /// Simulates the worker winning the retry-scheduling race: the UpdatedAt concurrency token
    /// makes the second writer's SaveChanges throw exactly this exception.
    /// </summary>
    private sealed class ConcurrencyConflictRetryPolicy : IStepExecutionRetryPolicy
    {
        public Task<StepFailureResolution> ResolveFailureAsync(
            WorkflowExecution execution,
            WorkflowStepExecution stepExecution,
            string stepType,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ScheduleRetryAsync(
            WorkflowStepExecution stepExecution,
            CancellationToken cancellationToken = default)
            => throw new DbUpdateConcurrencyException("step changed concurrently");
    }
}