using Microsoft.Extensions.Logging.Abstractions;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.UnitTests;

public class LogAuditStepProcessorTests
{
    private static (WorkflowStepExecution Execution, LogAuditStepProcessor Processor) Create(string config)
    {
        var step = WorkflowStep.Create(Guid.NewGuid(), 5, nameof(StepType.LogAudit), config);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 5);
        typeof(WorkflowStepExecution)
            .GetProperty(nameof(WorkflowStepExecution.WorkflowStep))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(execution, new object[] { step });
        execution.Start();

        return (execution, new LogAuditStepProcessor(NullLogger<LogAuditStepProcessor>.Instance));
    }

    [Fact]
    public async Task Logs_message_and_succeeds()
    {
        var (execution, processor) = Create("""{"message":"order shipped","logLevel":"information"}""");

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.True(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Succeeded, execution.Status);
        Assert.Equal("Audit logged: order shipped", execution.Output);
    }

    [Fact]
    public async Task Supports_non_default_log_levels()
    {
        var (execution, processor) = Create("""{"message":"warn me","logLevel":"warning"}""");

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.True(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Succeeded, execution.Status);
    }

    [Fact]
    public async Task Null_context_is_handled()
    {
        var (execution, processor) = Create("""{"message":"audited from null context","logLevel":"information"}""");

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.True(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Succeeded, execution.Status);
    }

    [Fact]
    public async Task Missing_message_throws()
    {
        var (execution, processor) = Create("""{"logLevel":"information"}""");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => processor.ProcessAsync(execution, null));
    }
}