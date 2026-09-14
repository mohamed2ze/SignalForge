using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.UnitTests.Services;

/// <summary>
/// Regression pins for L12: the step-tuple projections feeding execution aggregates are sampled to
/// a deterministic budget (maxAggregateStepTuples) so a huge window cannot exhaust memory. The
/// sampling keeps the most recent tuples (execution StartedAt descending) and drops the oldest.
/// </summary>
public class ExecutionObservabilityServiceTests
{
    private static SignalForgeDbContext CreateDb()
        => new(new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static void SetWorkflowStepNavigation(WorkflowStepExecution execution, WorkflowStep step)
    {
        typeof(WorkflowStepExecution)
            .GetProperty(nameof(WorkflowStepExecution.WorkflowStep))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(execution, new object[] { step });
    }

    [Fact]
    public async Task Aggregates_cap_step_tuples_to_the_most_recent_slice()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(Tenant.CreateWithId(tenantId, "obs-cap"));

        var workflow = Workflow.Create(tenantId, "obs-cap-wf");
        var version = workflow.CreateDraftVersion(1);
        version.AddStep(1, nameof(StepType.LogAudit), "{}", "audit");
        version.Publish();
        workflow.Enable();
        var step = version.Steps.Single();

        var baseUtc = DateTime.UtcNow;
        // 6 executions, each with one completed step. The OLDEST (i=1) carries the only retried
        // step (attempt 2); the other five are attempt 1.
        for (var i = 1; i <= 6; i++)
        {
            var evt = Event.Create(tenantId, $"cap-{Guid.NewGuid():N}", "order.created", baseUtc, "{}");
            var execution = WorkflowExecution.Create(workflow.Id, version.Id, evt.Id, tenantId);
            execution.Start();

            var stepExecution = WorkflowStepExecution.Create(execution.Id, step.Id, 1);
            stepExecution.Start();
            stepExecution.Succeed("audited");

            db.Events.Add(evt);
            db.WorkflowExecutions.Add(execution);
            db.WorkflowStepExecutions.Add(stepExecution);

            var startedAt = baseUtc.AddMinutes(i - 6); // i=1 oldest, i=6 newest
            db.Entry(execution).Property(x => x.StartedAt).CurrentValue = startedAt;
            db.Entry(stepExecution).Property(x => x.StartedAt).CurrentValue = startedAt;
            db.Entry(stepExecution).Property(x => x.CompletedAt).CurrentValue = startedAt.AddSeconds(1);
            if (i == 1)
                db.Entry(stepExecution).Property(x => x.AttemptNumber).CurrentValue = 2;

            SetWorkflowStepNavigation(stepExecution, step);
        }

        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync();

        var service = new ExecutionObservabilityService(db, maxAggregateStepTuples: 5);
        var aggregates = await service.GetAggregatesAsync(tenantId, new AggregateQueryFilter(null, null, null));

        // Latency is computed from the 5 most recent tuples only.
        var latency = Assert.Single(aggregates.StepLatency);
        Assert.Equal(nameof(StepType.LogAudit), latency.StepType);
        Assert.Equal(5, latency.Count);

        // The excluded tuple was the only retried one — sampling drops it deterministically.
        Assert.Equal(0, aggregates.Failures.TotalStepRetries);
    }

    [Fact]
    public async Task Under_the_cap_all_step_tuples_are_aggregated()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(Tenant.CreateWithId(tenantId, "obs-no-cap"));

        var workflow = Workflow.Create(tenantId, "obs-no-cap-wf");
        var version = workflow.CreateDraftVersion(1);
        version.AddStep(1, nameof(StepType.Delay), """{"seconds":1}""", "wait");
        version.Publish();
        workflow.Enable();
        var step = version.Steps.Single();

        var baseUtc = DateTime.UtcNow.AddMinutes(-1);
        for (var i = 1; i <= 3; i++)
        {
            var evt = Event.Create(tenantId, $"nocap-{Guid.NewGuid():N}", "order.created", baseUtc, "{}");
            var execution = WorkflowExecution.Create(workflow.Id, version.Id, evt.Id, tenantId);
            execution.Start();

            var stepExecution = WorkflowStepExecution.Create(execution.Id, step.Id, 1);
            stepExecution.Start();
            stepExecution.Succeed("waited");

            var startedAt = baseUtc.AddMinutes(i);
            db.Events.Add(evt);
            db.WorkflowExecutions.Add(execution);
            db.WorkflowStepExecutions.Add(stepExecution);
            db.Entry(execution).Property(x => x.StartedAt).CurrentValue = startedAt;
            db.Entry(stepExecution).Property(x => x.StartedAt).CurrentValue = startedAt;
            db.Entry(stepExecution).Property(x => x.CompletedAt).CurrentValue = startedAt.AddSeconds(1);
            SetWorkflowStepNavigation(stepExecution, step);
        }

        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync();

        var service = new ExecutionObservabilityService(db); // default 50k cap
        var aggregates = await service.GetAggregatesAsync(tenantId, new AggregateQueryFilter(null, null, null));

        var latency = Assert.Single(aggregates.StepLatency);
        Assert.Equal(3, latency.Count);
    }
}