using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalForge.Application.Data;
using SignalForge.Application.Notifications;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;
using SignalForge.Worker.Services;

namespace SignalForge.IntegrationTests;

/// <summary>
/// C4: the workflow execution pump's lease/claim guard against the real SQL Server, where the
/// atomic <c>ExecuteUpdateAsync</c> claim path is exercised. Multiple pump instances polling the
/// same store must never advance the same execution in the same cycle — every seeded execution
/// must end up with exactly one step execution.
/// </summary>
[Collection(MsSqlCollection.Name)]
public class WorkflowExecutionPumpConcurrencyTests : ApiTestBase
{
    private static readonly Guid TenantId = Guid.NewGuid();

    public WorkflowExecutionPumpConcurrencyTests(MsSqlContainerFixture database)
        : base(database)
    {
    }

    private static IServiceScopeFactory BuildScopeFactory(DbContextOptions<SignalForgeDbContext> options)
    {
        var registry = new NotificationProviderRegistry(
        [
            new EmailNotificationProvider(new RecordingEmailTransport()),
            new SmsNotificationProvider(new RecordingSmsTransport())
        ]);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ISignalForgeDbContext>(_ => new SignalForgeDbContext(options));
        services.AddScoped<IWorkflowExecutionAdvancer, WorkflowExecutionAdvancer>();
        services.AddScoped<IStepExecutionRetryPolicy, StepExecutionRetryPolicy>();
        services.AddScoped<IWorkflowExecutionOrchestratorService, WorkflowExecutionOrchestratorService>();
        services.AddSingleton(_ => new DelayStepProcessor(NullLogger<DelayStepProcessor>.Instance));
        services.AddSingleton(_ => new ConditionalStepProcessor(NullLogger<ConditionalStepProcessor>.Instance));
        services.AddSingleton(_ => new LogAuditStepProcessor(NullLogger<LogAuditStepProcessor>.Instance));
        services.AddSingleton<INotificationProviderRegistry>(_ => registry);
        services.AddSingleton(_ => new NotificationStepProcessor(
            registry, NullLogger<NotificationStepProcessor>.Instance));
        services.AddSingleton<IStepProcessorRegistry>(sp => new StepProcessorRegistry(
        [
            sp.GetRequiredService<DelayStepProcessor>(),
            sp.GetRequiredService<ConditionalStepProcessor>(),
            sp.GetRequiredService<LogAuditStepProcessor>(),
            sp.GetRequiredService<NotificationStepProcessor>()
        ]));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task Concurrent_pumps_advance_each_execution_exactly_once()
    {
        var options = DbOptions();

        try
        {
            const int executionCount = 25;

            // Seed `executionCount` running executions, one one-step workflow each. The instant
            // step (LogAudit) is deliberately cheap so overlapping cycles are likely; the atomic
            // claim is the only thing guaranteeing single advancement.
            using (var ctx = new SignalForgeDbContext(options))
            {
                ctx.Tenants.Add(Tenant.CreateWithId(TenantId, "itest-pump-concurrency"));
                for (var i = 0; i < executionCount; i++)
                {
                    var workflow = Workflow.Create(TenantId, $"con-wf-{i}");
                    var version = workflow.CreateDraftVersion(1);
                    version.AddStep(1, nameof(StepType.LogAudit),
                        """{"message":"hi","logLevel":"information"}""", $"step-{i}");
                    workflow.Enable();
                    version.Publish();
                    var @event = Event.Create(TenantId, $"pump-{i}", "order.created", DateTime.UtcNow, "{}");
                    ctx.Workflows.Add(workflow);
                    ctx.Events.Add(@event);
                    var execution = WorkflowExecution.Create(workflow.Id, version.Id, @event.Id, TenantId);
                    execution.Start();
                    ctx.WorkflowExecutions.Add(execution);
                }

                await ctx.SaveChangesAsync();
            }

            var pumpOptions = new ExecutionPumpOptions { BatchSize = 50, ClaimLeaseSeconds = 300 };

            // Four independent workers over the same store, hammering cycles concurrently.
            var pumps = Enumerable.Range(0, 4)
                .Select(_ => new WorkflowExecutionPump(
                    BuildScopeFactory(options), Options.Create(pumpOptions), NullLogger<WorkflowExecutionPump>.Instance))
                .ToArray();

            for (var cycle = 0; cycle < 12; cycle++)
            {
                await Task.WhenAll(pumps.Select(p => p.ProcessCycleAsync(CancellationToken.None)));
            }

            using var verify = new SignalForgeDbContext(options);
            var stepExecutions = await verify.WorkflowStepExecutions
                .Where(se => verify.WorkflowExecutions.Any(e => e.Id == se.WorkflowExecutionId))
                .ToListAsync();

            // Exactly one step execution per seeded execution: no double-start, no skipped final
            // transition, and no execution left mid-flight.
            Assert.Equal(executionCount, stepExecutions.Count);
            Assert.Equal(executionCount,
                await verify.WorkflowExecutions.CountAsync(e => e.Status == WorkflowExecutionStatus.Succeeded));
            Assert.Equal(executionCount, stepExecutions.Select(se => se.WorkflowExecutionId).Distinct().Count());
        }
        finally
        {
            await CleanupAsync(TenantId);
        }
    }

    /// <summary>
    /// Fault injection for the item-1 claim (worker dies mid-step, the lease lapses, another
    /// worker recovers): simulate a worker that claimed an execution and started step 1 — the
    /// advancer's durable effect before it processes anything — then "died" (a fresh claim plus a
    /// live Running step persisted against the real store). After the claim-lease window elapses
    /// the next worker must reclaim, requeue the stale Running step, and complete it without
    /// duplicating work or skipping the final transition — all against SQL Server, including the
    /// rowwise concurrency-token UPDATEs introduced with the race-proofing work.
    /// </summary>
    [Fact]
    public async Task Worker_crash_after_step_start_is_requeued_and_completed_by_the_next_worker()
    {
        var options = DbOptions();
        var crashPoint = DateTime.UtcNow;

        try
        {
            Guid workflowStepId;
            using (var ctx = new SignalForgeDbContext(options))
            {
                ctx.Tenants.Add(Tenant.CreateWithId(TenantId, "itest-pump-crash"));
                var workflow = Workflow.Create(TenantId, "crash-wf");
                var version = workflow.CreateDraftVersion(1);
                version.AddStep(1, nameof(StepType.LogAudit),
                    """{"message":"crash","logLevel":"information"}""", "step-1");
                workflow.Enable();
                version.Publish();
                var @event = Event.Create(TenantId, "crash-evt", "order.created", DateTime.UtcNow, "{}");
                ctx.Workflows.Add(workflow);
                ctx.Events.Add(@event);
                var execution = WorkflowExecution.Create(workflow.Id, version.Id, @event.Id, TenantId);
                execution.Start();
                ctx.WorkflowExecutions.Add(execution);
                await ctx.SaveChangesAsync();
                workflowStepId = version.Steps.Single().Id;
            }

            // The crashed worker's durable effect: claimed execution + a Running step that was
            // started but never processed. Same shape the advancer leaves after the start-save.
            using (var ctx = new SignalForgeDbContext(options))
            {
                var execution = await ctx.WorkflowExecutions.SingleAsync();
                execution.Claim(crashPoint);
                var step = WorkflowStepExecution.Create(execution.Id, workflowStepId, 1);
                step.Start();
                ctx.WorkflowStepExecutions.Add(step);
                await ctx.SaveChangesAsync();
            }

            // Prove the crash state landed in the store as a live in-flight step.
            using (var ctx = new SignalForgeDbContext(options))
            {
                var execution = await ctx.WorkflowExecutions.SingleAsync();
                Assert.NotNull(execution.ClaimedAt);
                Assert.Equal(WorkflowStepExecutionStatus.Running,
                    (await ctx.WorkflowStepExecutions.SingleAsync()).Status);
            }

            // The dead worker never wakes; wall-clock advances past the 300s claim lease.
            using (var ctx = new SignalForgeDbContext(options))
            {
                var execution = await ctx.WorkflowExecutions.SingleAsync();
                ctx.Entry(execution).Property(e => e.ClaimedAt).CurrentValue = crashPoint.AddSeconds(-301);
                var step = await ctx.WorkflowStepExecutions.SingleAsync();
                ctx.Entry(step).Property(s => s.StartedAt).CurrentValue = crashPoint.AddSeconds(-301);
                await ctx.SaveChangesAsync();
            }

            // The next worker reclaims, requeues the stale step, and runs it; one more cycle
            // then completes the (single-step) execution.
            var pump = new WorkflowExecutionPump(
                BuildScopeFactory(options),
                Options.Create(new ExecutionPumpOptions { BatchSize = 10, ClaimLeaseSeconds = 300 }),
                NullLogger<WorkflowExecutionPump>.Instance);
            await pump.ProcessCycleAsync(CancellationToken.None);
            await pump.ProcessCycleAsync(CancellationToken.None);

            using (var verify = new SignalForgeDbContext(options))
            {
                var execution = await verify.WorkflowExecutions.SingleAsync();
                Assert.Equal(WorkflowExecutionStatus.Succeeded, execution.Status);
                Assert.Null(execution.ClaimedAt); // lease released after the advance
                var step = await verify.WorkflowStepExecutions.SingleAsync();
                Assert.Equal(WorkflowStepExecutionStatus.Succeeded, step.Status);
                // The requeued recovery run is a fresh attempt (the crashed worker consumed one).
                Assert.Equal(2, step.AttemptNumber);
            }

            // A further cycle must not double-complete from the recovered state.
            await pump.ProcessCycleAsync(CancellationToken.None);
            using (var verify = new SignalForgeDbContext(options))
            {
                Assert.Empty(await verify.WorkflowExecutions
                    .Where(e => e.Status != WorkflowExecutionStatus.Succeeded).ToListAsync());
                Assert.Equal(1, await verify.WorkflowStepExecutions.CountAsync());
            }
        }
        finally
        {
            await CleanupAsync(TenantId);
        }
    }

    /// <summary>
    /// Fault injection for the item-6 claim (a conditional branch routes the execution back onto
    /// an already-completed step — the old code threw out of PrepareForRetry every poll, wedging
    /// the execution and burning pump cycles forever): drive the real cyclic workflow through the
    /// real pump against SQL Server and assert it terminates with exactly one dead letter and a
    /// Failed execution, then stays terminal across further polls.
    /// </summary>
    [Fact]
    public async Task Cyclic_route_onto_a_completed_step_terminates_with_one_dead_letter()
    {
        var options = DbOptions();

        try
        {
            using (var ctx = new SignalForgeDbContext(options))
            {
                ctx.Tenants.Add(Tenant.CreateWithId(TenantId, "itest-pump-poison"));
                var workflow = Workflow.Create(TenantId, "poison-wf");
                var version = workflow.CreateDraftVersion(1);
                version.AddStep(1, nameof(StepType.LogAudit), """{"message":"one","logLevel":"information"}""", "s1");
                version.AddStep(2, nameof(StepType.LogAudit), """{"message":"two","logLevel":"information"}""", "s2");
                // Step 3 branches back to step 2 on the marker — i.e. onto an already-completed
                // step (next becomes step 3, which just Succeeded). Re-entry can never finish.
                version.AddStep(3, nameof(StepType.Conditional),
                    """{"expression":"$.event.payload.loop == 'yes'","trueStep":2,"falseStep":3}""", "s3");
                workflow.Enable();
                version.Publish();
                var @event = Event.Create(TenantId, "poison-evt", "order.created", DateTime.UtcNow, """{"loop":"yes"}""");
                ctx.Workflows.Add(workflow);
                ctx.Events.Add(@event);
                var execution = WorkflowExecution.Create(workflow.Id, version.Id, @event.Id, TenantId);
                execution.Start();
                ctx.WorkflowExecutions.Add(execution);
                await ctx.SaveChangesAsync();
            }

            var pump = new WorkflowExecutionPump(
                BuildScopeFactory(options),
                Options.Create(new ExecutionPumpOptions { BatchSize = 10, ClaimLeaseSeconds = 300 }),
                NullLogger<WorkflowExecutionPump>.Instance);

            // Drain generously: steps 1-3, the poison branch, then slack cycles to prove the
            // failed execution is left alone rather than re-polled.
            for (var cycle = 0; cycle < 8; cycle++)
                await pump.ProcessCycleAsync(CancellationToken.None);

            using (var verify = new SignalForgeDbContext(options))
            {
                var execution = await verify.WorkflowExecutions.SingleAsync();
                Assert.Equal(WorkflowExecutionStatus.Failed, execution.Status);
                Assert.Equal(3, await verify.WorkflowStepExecutions.CountAsync());
                Assert.All(await verify.WorkflowStepExecutions.ToListAsync(),
                    se => Assert.Equal(WorkflowStepExecutionStatus.Succeeded, se.Status));
                var deadLetter = await verify.DeadLetterMessages.SingleAsync();
                Assert.Equal(nameof(StepType.Conditional), deadLetter.FailedStepType);
                Assert.Equal(execution.Id, deadLetter.WorkflowExecutionId);
            }

            // Terminal: more polls change nothing and never mint additional dead letters.
            for (var cycle = 0; cycle < 4; cycle++)
                await pump.ProcessCycleAsync(CancellationToken.None);
            using (var verify = new SignalForgeDbContext(options))
            {
                Assert.Equal(WorkflowExecutionStatus.Failed,
                    (await verify.WorkflowExecutions.SingleAsync()).Status);
                Assert.Equal(1, await verify.DeadLetterMessages.CountAsync());
            }
        }
        finally
        {
            await CleanupAsync(TenantId);
        }
    }
}