using SignalForge.Domain.Models;

namespace SignalForge.UnitTests.Domain;

public class WorkflowStepExecutionTests
{
    private static WorkflowStepExecution CreateExecution(int maxAttempts = 3)
        => WorkflowStepExecution.Create(Guid.NewGuid(), Guid.NewGuid(), 1, maxAttempts);

    [Fact]
    public void Create_sets_defaults()
    {
        var execution = CreateExecution();

        Assert.Equal(WorkflowStepExecutionStatus.Pending, execution.Status);
        Assert.Equal(0, execution.AttemptNumber);
        Assert.Equal(3, execution.MaxAttempts);
        Assert.NotEqual(default, execution.StartedAt);
        Assert.Null(execution.CompletedAt);
        Assert.Null(execution.RouteToStepNumber);
        Assert.False(execution.IsRunning());
        Assert.False(execution.IsCompleted());
        Assert.False(execution.CanRetry());
    }

    [Fact]
    public void Start_moves_to_running_and_increments_attempt()
    {
        var execution = CreateExecution();

        execution.Start();

        Assert.Equal(WorkflowStepExecutionStatus.Running, execution.Status);
        Assert.Equal(1, execution.AttemptNumber);
        Assert.True(execution.IsRunning());
    }

    [Fact]
    public void Start_rejects_non_pending_execution()
    {
        var execution = CreateExecution();
        execution.Start();

        Assert.Throws<InvalidOperationException>(() => execution.Start());
    }

    [Fact]
    public void Succeed_records_output_and_route()
    {
        var execution = CreateExecution();
        execution.Start();

        execution.Succeed("""{"result":1}""", routeToStepNumber: 7);

        Assert.Equal(WorkflowStepExecutionStatus.Succeeded, execution.Status);
        Assert.Equal("""{"result":1}""", execution.Output);
        Assert.Equal(7, execution.RouteToStepNumber);
        Assert.NotNull(execution.CompletedAt);
        Assert.True(execution.IsCompleted());
    }

    [Fact]
    public void Succeed_defaults_route_to_null()
    {
        var execution = CreateExecution();
        execution.Start();

        execution.Succeed("output");

        Assert.Null(execution.RouteToStepNumber);
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

        execution.Fail("step failed");

        Assert.Equal(WorkflowStepExecutionStatus.Failed, execution.Status);
        Assert.Equal("step failed", execution.ErrorMessage);
        Assert.True(execution.IsCompleted());
    }

    [Fact]
    public void Fail_rejects_blank_error_message()
    {
        var execution = CreateExecution();
        execution.Start();

        Assert.Throws<ArgumentException>(() => execution.Fail("  "));
    }

    [Fact]
    public void Fail_rejects_non_running_execution()
    {
        var execution = CreateExecution();

        Assert.Throws<InvalidOperationException>(() => execution.Fail("boom"));
    }

    [Fact]
    public void Cancel_cancels_execution()
    {
        var execution = CreateExecution();
        execution.Start();

        execution.Cancel();

        Assert.Equal(WorkflowStepExecutionStatus.Cancelled, execution.Status);
        Assert.True(execution.IsCompleted());
    }

    [Fact]
    public void Retry_schedules_retry_after_failure()
    {
        var execution = CreateExecution();
        var due = DateTime.UtcNow.AddSeconds(30);

        execution.Start();
        execution.Fail("boom");
        execution.Retry(due);

        Assert.Equal(WorkflowStepExecutionStatus.Retrying, execution.Status);
        Assert.Equal(due, execution.NextRetryAt);
        Assert.True(execution.IsTimeToRetry() == false);
    }

    [Fact]
    public void Retry_rejects_non_failed_execution()
    {
        var execution = CreateExecution();

        Assert.Throws<InvalidOperationException>(() => execution.Retry(DateTime.UtcNow));
    }

    [Fact]
    public void PrepareForRetry_returns_to_pending()
    {
        var execution = CreateExecution();
        execution.Start();
        execution.Fail("boom");
        execution.Retry(DateTime.UtcNow.AddSeconds(1));

        execution.PrepareForRetry();

        Assert.Equal(WorkflowStepExecutionStatus.Pending, execution.Status);
    }

    [Fact]
    public void Retry_budget_is_bounded_by_max_attempts()
    {
        var execution = CreateExecution(maxAttempts: 3);

        execution.Start();
        execution.Fail("1");
        Assert.True(execution.CanRetry());
        execution.Retry(DateTime.UtcNow.AddSeconds(1));
        execution.PrepareForRetry();

        execution.Start();
        Assert.Equal(2, execution.AttemptNumber);
        execution.Fail("2");
        Assert.True(execution.CanRetry());
        execution.Retry(DateTime.UtcNow.AddSeconds(1));
        execution.PrepareForRetry();

        execution.Start();
        Assert.Equal(3, execution.AttemptNumber);
        execution.Fail("3");
        Assert.False(execution.CanRetry());
        Assert.Throws<InvalidOperationException>(() => execution.Retry(DateTime.UtcNow.AddSeconds(1)));
    }

    [Fact]
    public void IsTimeToRetry_is_true_when_due()
    {
        var execution = CreateExecution();
        execution.Start();
        execution.Fail("boom");
        execution.Retry(DateTime.UtcNow.AddMilliseconds(-1));

        Assert.True(execution.IsTimeToRetry());
    }
}