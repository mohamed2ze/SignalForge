using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.IntegrationTests;

/// <summary>
/// C5 end-to-end: a RetryableOperation step is dispatched through the production DI graph (the
/// real <c>AddApplicationServices</c> registrations, including the operation registry) against SQL
/// Server — proving the echo operation executes and the step succeeds through the orchestrator.
/// </summary>
[Collection(MsSqlCollection.Name)]
public class RetryableOperationExecutionTests : ApiTestBase
{
    private static readonly Guid TenantId = Guid.NewGuid();

    public RetryableOperationExecutionTests(MsSqlContainerFixture database)
        : base(database)
    {
    }

    [Fact]
    public async Task RetryableOperationStep_Dispatch_To_Registered_Operation_Against_Real_Db()
    {
        try
        {
            using var ctx = new SignalForgeDbContext(DbOptions());
            ctx.Tenants.Add(Tenant.CreateWithId(TenantId, "itest-retryable-op"));
            var workflow = Workflow.Create(TenantId, "retryable-workflow");
            var version = workflow.CreateDraftVersion(1);
            version.AddStep(1, nameof(StepType.RetryableOperation),
                """{"operationType":"echo","parameters":{"table":"orders","row":42}}""", "echo-op");
            workflow.Enable();
            version.Publish();
            var @event = Event.Create(TenantId, $"retry-{Guid.NewGuid():N}", "order.created", DateTime.UtcNow, "{}");
            ctx.Workflows.Add(workflow);
            ctx.Events.Add(@event);
            await ctx.SaveChangesAsync();

            using var scope = Factory.Services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IWorkflowExecutionOrchestratorService>();

            var executionId = (await orchestrator.StartWorkflowExecutionAsync(
                workflow.Id, version.Id, @event.Id, TenantId)).Id;

            var advanced = await orchestrator.AdvanceWorkflowExecutionAsync(executionId, CancellationToken.None);
            Assert.True(advanced);

            using var verify = new SignalForgeDbContext(DbOptions());
            var stepExecution = await verify.WorkflowStepExecutions
                .SingleAsync(se => se.WorkflowExecutionId == executionId);
            Assert.Equal(WorkflowStepExecutionStatus.Succeeded, stepExecution.Status);
            Assert.Contains("orders", stepExecution.Output);

            // Second advance sees no steps left and marks the execution succeeded.
            var finalAdvance = await orchestrator.AdvanceWorkflowExecutionAsync(executionId, CancellationToken.None);
            Assert.True(finalAdvance);

            var finished = await verify.WorkflowExecutions.SingleAsync(e => e.Id == executionId);
            Assert.Equal(WorkflowExecutionStatus.Succeeded, finished.Status);
        }
        finally
        {
            await CleanupAsync(TenantId);
        }
    }
}