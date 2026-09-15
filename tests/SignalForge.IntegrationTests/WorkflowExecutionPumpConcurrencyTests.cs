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
}