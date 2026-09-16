using Microsoft.EntityFrameworkCore;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.UnitTests.Services;

/// <summary>
/// The UpdateAt concurrency token (rowversion equivalent) on workflow executions and step
/// executions makes a stale writer's commit fail with <see cref="DbUpdateConcurrencyException"/>.
/// This is what turns the HTTP schedule-retry endpoint's write-vs-worker race into a controlled
/// 409 instead of a silently-lost update or a 500.
/// </summary>
public class WorkflowExecutionConcurrencyTokenTests
{
    private static DbContextOptions<SignalForgeDbContext> NewSharedOptions(string dbName)
        => new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

    private static async Task<Guid> SeedExecutionAsync(DbContextOptions<SignalForgeDbContext> options)
    {
        var tenantId = Guid.NewGuid();
        var workflow = Workflow.Create(tenantId, "ct-wf");
        var version = workflow.CreateDraftVersion(1);
        version.AddStep(1, "LogAudit", """{"message":"hi","logLevel":"information"}""");
        workflow.Enable();
        version.Publish();
        var @event = Event.Create(tenantId, $"ext-{tenantId:N}", "order.created", DateTime.UtcNow, "{}");

        var executionId = Guid.Empty;
        using (var db = new SignalForgeDbContext(options))
        {
            db.Tenants.Add(Tenant.CreateWithId(tenantId, "ct-test"));
            db.Workflows.Add(workflow);
            db.Events.Add(@event);
            await db.SaveChangesAsync();

            var execution = WorkflowExecution.Create(workflow.Id, version.Id, @event.Id, tenantId);
            execution.Start();
            db.WorkflowExecutions.Add(execution);
            await db.SaveChangesAsync();
            executionId = execution.Id;
        }

        return executionId;
    }

    [Fact]
    public async Task Stale_workflow_execution_commit_throws_DbUpdateConcurrencyException()
    {
        var options = NewSharedOptions(Guid.NewGuid().ToString());
        var executionId = await SeedExecutionAsync(options);

        Guid executionTenant;
        using (var httpScope = new SignalForgeDbContext(options))
        {
            // The HTTP request reads the execution, then does its work in memory...
            var httpExecution = await httpScope.WorkflowExecutions.SingleAsync(e => e.Id == executionId);
            executionTenant = httpExecution.TenantId;

            // ...while the worker claims it and commits (bumps UpdatedAt).
            using (var workerScope = new SignalForgeDbContext(options))
            {
                var workerExecution = await workerScope.WorkflowExecutions.SingleAsync(e => e.Id == executionId);
                workerExecution.Claim(DateTime.UtcNow);
                await workerScope.SaveChangesAsync();
            }

            // The stale client-side writer must not silently clobber the worker's commit.
            httpExecution.ReleaseClaim();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => httpScope.SaveChangesAsync());
        }

        // The worker's commit survived untouched.
        using (var verify = new SignalForgeDbContext(options))
        {
            var execution = await verify.WorkflowExecutions.SingleAsync(e => e.Id == executionId);
            Assert.NotNull(execution.ClaimedAt);
            Assert.Equal(executionTenant, execution.TenantId);
        }
    }

    [Fact]
    public async Task Stale_workflow_step_execution_commit_throws_DbUpdateConcurrencyException()
    {
        var options = NewSharedOptions(Guid.NewGuid().ToString());
        var executionId = await SeedExecutionAsync(options);

        Guid stepExecutionId;
        using (var httpScope = new SignalForgeDbContext(options))
        {
            var httpExecution = await httpScope.WorkflowExecutions.SingleAsync(e => e.Id == executionId);
            var step = WorkflowStepExecution.Create(httpExecution.Id, Guid.NewGuid(), 1, maxAttempts: 3);
            step.Start();
            step.Fail("attempt 1 failed");
            httpScope.WorkflowStepExecutions.Add(step);
            await httpScope.SaveChangesAsync();
            stepExecutionId = step.Id;
        }

        using (var httpScope = new SignalForgeDbContext(options))
        {
            var httpStep = await httpScope.WorkflowStepExecutions.SingleAsync(s => s.Id == stepExecutionId);

            using (var workerScope = new SignalForgeDbContext(options))
            {
                var workerStep = await workerScope.WorkflowStepExecutions.SingleAsync(s => s.Id == stepExecutionId);
                workerStep.Retry(DateTime.UtcNow.AddSeconds(30)); // worker retries first
                await workerScope.SaveChangesAsync();
            }

            // The HTTP retry request now schedules its own (stale) retry: its commit must
            // conflict with the worker's, not silently overwrite the worker's schedule.
            httpStep.Retry(DateTime.UtcNow.AddSeconds(60));
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                httpScope.SaveChangesAsync());
        }

        using (var verify = new SignalForgeDbContext(options))
        {
            var step = await verify.WorkflowStepExecutions.SingleAsync(s => s.Id == stepExecutionId);
            Assert.Equal(WorkflowStepExecutionStatus.Retrying, step.Status);
        }
    }
}