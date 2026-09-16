using SignalForge.Domain.Models;

namespace SignalForge.UnitTests.Domain;

public class WorkflowExecutionTests
{
    private static WorkflowExecution CreateExecution()
        => WorkflowExecution.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Create_sets_defaults()
    {
        var workflowId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var execution = WorkflowExecution.Create(workflowId, versionId, eventId, tenantId);

        Assert.Equal(workflowId, execution.WorkflowId);
        Assert.Equal(versionId, execution.WorkflowVersionId);
        Assert.Equal(eventId, execution.EventId);
        Assert.Equal(tenantId, execution.TenantId);
        Assert.Equal(WorkflowExecutionStatus.Pending, execution.Status);
        Assert.Equal(0, execution.CurrentStepNumber);
        Assert.Equal(0, execution.RetryCount);
        Assert.Null(execution.CompletedAt);
        Assert.False(execution.IsCompleted());
    }

    [Fact]
    public void Start_transitions_to_running()
    {
        var execution = CreateExecution();
        execution.Start();

        Assert.Equal(WorkflowExecutionStatus.Running, execution.Status);
        Assert.True(execution.IsRunning());
        Assert.False(execution.IsCompleted());
    }

    [Fact]
    public void Start_rejects_non_pending_execution()
    {
        var execution = CreateExecution();
        execution.Start();

        Assert.Throws<InvalidOperationException>(() => execution.Start());
    }

    [Fact]
    public void Succeed_completes_execution()
    {
        var execution = CreateExecution();
        execution.Start();

        execution.Succeed();

        Assert.Equal(WorkflowExecutionStatus.Succeeded, execution.Status);
        Assert.NotNull(execution.CompletedAt);
        Assert.True(execution.IsCompleted());
    }

    [Fact]
    public void Succeed_rejects_non_running_execution()
    {
        var execution = CreateExecution();

        Assert.Throws<InvalidOperationException>(() => execution.Succeed());
    }

    [Fact]
    public void Fail_records_error()
    {
        var execution = CreateExecution();
        execution.Start();

        execution.Fail("step exploded");

        Assert.Equal(WorkflowExecutionStatus.Failed, execution.Status);
        Assert.Equal("step exploded", execution.ErrorMessage);
        Assert.NotNull(execution.CompletedAt);
        Assert.True(execution.IsCompleted());
    }

    [Fact]
    public void Cancel_cancels_execution()
    {
        var execution = CreateExecution();
        execution.Start();

        execution.Cancel();

        Assert.Equal(WorkflowExecutionStatus.Cancelled, execution.Status);
        Assert.True(execution.IsCompleted());
    }

    [Fact]
    public void AdvanceStep_increments_only_when_running()
    {
        var execution = CreateExecution();

        Assert.Throws<InvalidOperationException>(() => execution.AdvanceStep());

        execution.Start();
        execution.AdvanceStep();

        Assert.Equal(1, execution.CurrentStepNumber);
    }

    [Fact]
    public void RouteToStep_jumps_to_absolute_step()
    {
        var execution = CreateExecution();
        execution.Start();

        execution.RouteToStep(5);

        Assert.Equal(5, execution.CurrentStepNumber);
    }

    [Fact]
    public void RouteToStep_rejects_non_positive_target()
    {
        var execution = CreateExecution();
        execution.Start();

        Assert.Throws<ArgumentOutOfRangeException>(() => execution.RouteToStep(0));
    }

    [Fact]
    public void ResetForRetry_returns_failed_execution_to_pending()
    {
        var execution = CreateExecution();
        execution.Start();
        execution.AdvanceStep();
        execution.Fail("boom");

        execution.ResetForRetry();

        Assert.Equal(WorkflowExecutionStatus.Pending, execution.Status);
        Assert.Equal(0, execution.CurrentStepNumber);
        Assert.Null(execution.CompletedAt);
        Assert.Null(execution.ErrorMessage);
    }

    [Fact]
    public void ResetForRetry_returns_cancelled_execution_to_pending()
    {
        var execution = CreateExecution();
        execution.Start();
        execution.Cancel();

        execution.ResetForRetry();

        Assert.Equal(WorkflowExecutionStatus.Pending, execution.Status);
    }

    [Fact]
    public void ResetForRetry_rejects_running_execution()
    {
        var execution = CreateExecution();
        execution.Start();

        Assert.Throws<InvalidOperationException>(() => execution.ResetForRetry());
    }
}