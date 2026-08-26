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

namespace SignalForge.UnitTests.Services;

/// <summary>
/// Deterministic tests for <see cref="WorkflowExecutionPump"/>. Like the outbox processor the
/// pump never sleeps: it returns the next poll delay, so selection, advancement and pacing are
/// all observable through the return value without any <see cref="Task.Delay"/>.
/// </summary>
public class WorkflowExecutionPumpTests
{
    private static DbContextOptions<SignalForgeDbContext> NewInMemoryOptions()
        => new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static IServiceScopeFactory BuildScopeFactory(DbContextOptions<SignalForgeDbContext> options)
    {
        var registry = new NotificationProviderRegistry(
        [
            new EmailNotificationProvider(NullLogger<EmailNotificationProvider>.Instance),
            new SmsNotificationProvider(NullLogger<SmsNotificationProvider>.Instance)
        ]);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ISignalForgeDbContext>(_ => new SignalForgeDbContext(options));
        services.AddScoped<IWorkflowExecutionOrchestratorService, WorkflowExecutionOrchestratorService>();
        services.AddSingleton(_ => new DelayStepProcessor(NullLogger<DelayStepProcessor>.Instance));
        services.AddSingleton(_ => new ConditionalStepProcessor(NullLogger<ConditionalStepProcessor>.Instance));
        services.AddSingleton(_ => new LogAuditStepProcessor(NullLogger<LogAuditStepProcessor>.Instance));
        services.AddSingleton<INotificationProviderRegistry>(_ => registry);
        services.AddSingleton(_ => new NotificationStepProcessor(
            registry, NullLogger<NotificationStepProcessor>.Instance));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static WorkflowExecutionPump CreatePump(
        IServiceScopeFactory scopeFactory,
        ExecutionPumpOptions? options = null)
    {
        options ??= new ExecutionPumpOptions();
        return new WorkflowExecutionPump(
            scopeFactory,
            Options.Create(options),
            NullLogger<WorkflowExecutionPump>.Instance);
    }

    private static async Task<(Guid WorkflowId, Guid VersionId, Guid EventId, Guid TenantId, Guid StepId)> SeedAsync(
        IServiceScopeFactory scopeFactory,
        Action<WorkflowVersion> configure,
        bool startExecution)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IWorkflowExecutionOrchestratorService>();

        var tenantId = Guid.NewGuid();
        db.Tenants.Add(Tenant.CreateWithId(tenantId, "pump-test"));

        var workflow = Workflow.Create(tenantId, "pump-wf");
        var version = workflow.CreateDraftVersion(1);
        configure(version);
        workflow.Enable();
        version.Publish();

        var @event = Event.Create(tenantId, $"ext-{tenantId:N}", "order.created", DateTime.UtcNow, "{}");
        db.Workflows.Add(workflow);
        db.Events.Add(@event);
        await db.SaveChangesAsync();

        if (startExecution)
        {
            await orchestrator.StartWorkflowExecutionAsync(
                workflow.Id, version.Id, @event.Id, tenantId);
        }

        return (workflow.Id, version.Id, @event.Id, tenantId, version.Steps.First().Id);
    }

    [Fact]
    public async Task Empty_store_returns_interval_without_advancing_anything()
    {
        var options = NewInMemoryOptions();
        var scopeFactory = BuildScopeFactory(options);
        var pump = CreatePump(scopeFactory);

        var delay = await pump.ProcessCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(2), delay);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();
        Assert.Empty(await db.WorkflowStepExecutions.ToListAsync());
    }

    [Fact]
    public async Task Multi_step_workflow_completes_across_cycles_without_duplicate_steps()
    {
        var options = NewInMemoryOptions();
        var scopeFactory = BuildScopeFactory(options);
        await SeedAsync(scopeFactory, version =>
        {
            version.AddStep(1, nameof(StepType.LogAudit), """{"message":"one","logLevel":"information"}""");
            version.AddStep(2, nameof(StepType.LogAudit), """{"message":"two","logLevel":"information"}""");
            version.AddStep(3, nameof(StepType.LogAudit), """{"message":"three","logLevel":"information"}""");
        }, startExecution: true);

        var pump = CreatePump(scopeFactory);

        // Steps 1..3: one advancement per cycle.
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            await pump.ProcessCycleAsync(CancellationToken.None);

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();
            var execution = await db.WorkflowExecutions.SingleAsync();
            Assert.Equal(cycle, execution.CurrentStepNumber);
            Assert.Equal(cycle, await db.WorkflowStepExecutions.CountAsync()); // no duplicates
        }

        // Final cycle: no more steps -> execution succeeds.
        await pump.ProcessCycleAsync(CancellationToken.None);

        using var finalscope = scopeFactory.CreateScope();
        var finalDb = finalscope.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();
        var finished = await finalDb.WorkflowExecutions.SingleAsync();
        Assert.Equal(WorkflowExecutionStatus.Succeeded, finished.Status);
        Assert.True(finished.IsCompleted());
        Assert.All(await finalDb.WorkflowStepExecutions.ToListAsync(),
            se => Assert.Equal(WorkflowStepExecutionStatus.Succeeded, se.Status));
    }

    [Fact]
    public async Task Does_not_start_a_step_that_is_already_in_flight()
    {
        var options = NewInMemoryOptions();
        var scopeFactory = BuildScopeFactory(options);
        var (_, versionId, eventId, tenantId, stepId) = await SeedAsync(
            scopeFactory, v => v.AddStep(1, nameof(StepType.LogAudit), """{"message":"hi","logLevel":"information"}"""),
            startExecution: true);

        // Manually mint a Pending step execution for step 1 to simulate an in-flight step.
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();
            var execution = await db.WorkflowExecutions.SingleAsync();
            db.WorkflowStepExecutions.Add(WorkflowStepExecution.Create(execution.Id, stepId, 1));
            await db.SaveChangesAsync();
        }

        var pump = CreatePump(scopeFactory);
        var delay = await pump.ProcessCycleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(2), delay);

        using var verify = scopeFactory.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();
        var after = await verifyDb.WorkflowExecutions.SingleAsync();
        Assert.Equal(0, after.CurrentStepNumber); // untouched: the in-flight step blocked the pump
        Assert.Single(await verifyDb.WorkflowStepExecutions.ToListAsync());
    }

    [Fact]
    public async Task Pump_reenters_failed_step_when_retry_window_opens()
    {
        var options = NewInMemoryOptions();
        var scopeFactory = BuildScopeFactory(options);
        await SeedAsync(scopeFactory,
            v => v.AddStep(1, nameof(StepType.NotificationSimulation), """{"type":"email"}"""),
            startExecution: true);

        var pump = CreatePump(scopeFactory);

        // Cycle 1: step attempt 1 fails deterministically (no recipient) -> Retrying + backoff.
        await pump.ProcessCycleAsync(CancellationToken.None);
        using (var scope = scopeFactory.CreateScope())
        {
            var db = (SignalForgeDbContext)scope.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();
            var step = await db.WorkflowStepExecutions.SingleAsync();
            Assert.Equal(WorkflowStepExecutionStatus.Retrying, step.Status);
            Assert.Equal(1, step.AttemptNumber);

            // Pin the retry window far in the future so "cycle 2" is deterministically not-due,
            // independent of wall-clock time.
            db.Entry(step).Property(s => s.NextRetryAt).CurrentValue = DateTime.UtcNow.AddHours(1);
            await db.SaveChangesAsync();
        }

        // Cycle 2: backoff not due -> pump skips (orchestrator returns busy), record untouched.
        await pump.ProcessCycleAsync(CancellationToken.None);

        // Open the retry window, then cycle 3 re-enters the SAME record in place.
        using (var scope = scopeFactory.CreateScope())
        {
            var db = (SignalForgeDbContext)scope.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();
            var step = await db.WorkflowStepExecutions.SingleAsync();
            db.Entry(step).Property(s => s.NextRetryAt).CurrentValue = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        _ = await pump.ProcessCycleAsync(CancellationToken.None);

        using var finalscope = scopeFactory.CreateScope();
        var finalDb = finalscope.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();
        var finalStep = await finalDb.WorkflowStepExecutions.SingleAsync();
        Assert.Equal(2, finalStep.AttemptNumber);
        Assert.Equal(WorkflowExecutionStatus.Running,
            (await finalDb.WorkflowExecutions.SingleAsync()).Status); // still orchestrating retries
    }
}