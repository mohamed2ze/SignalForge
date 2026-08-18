using Microsoft.Extensions.Logging.Abstractions;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.UnitTests;

public class DelayStepProcessorTests
{
    private static (WorkflowStepExecution Execution, DelayStepProcessor Processor) Create(string config)
    {
        var step = WorkflowStep.Create(Guid.NewGuid(), 4, nameof(StepType.Delay), config);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 4);
        typeof(WorkflowStepExecution)
            .GetProperty(nameof(WorkflowStepExecution.WorkflowStep))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(execution, new object[] { step });
        execution.Start();

        return (execution, new DelayStepProcessor(NullLogger<DelayStepProcessor>.Instance));
    }

    [Fact]
    public async Task Zero_second_delay_completes_immediately()
    {
        var (execution, processor) = Create("""{"seconds":0}""");

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.True(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Succeeded, execution.Status);
        Assert.Equal("Delayed for 0 seconds", execution.Output);
    }

    [Fact]
    public async Task Negative_delay_fails_the_step()
    {
        var (execution, processor) = Create("""{"seconds":-1}""");

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.False(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Failed, execution.Status);
        Assert.Contains("Delay step failed", execution.ErrorMessage);
    }

    [Fact]
    public async Task Non_integer_delay_throws()
    {
        var (execution, processor) = Create("""{"seconds":"abc"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(execution, null));
    }

    [Fact]
    public async Task Missing_seconds_throws()
    {
        var (execution, processor) = Create("""{"message":"no delay"}""");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => processor.ProcessAsync(execution, null));
    }

    [Fact]
    public async Task Cancellation_cancels_the_step()
    {
        var (execution, processor) = Create("""{"seconds":0}""");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        bool shouldContinue = await processor.ProcessAsync(execution, null, cts.Token);

        Assert.False(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Cancelled, execution.Status);
        Assert.True(execution.IsCompleted());
    }
}