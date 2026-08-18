using Microsoft.Extensions.Logging.Abstractions;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.UnitTests;

public class RetryableOperationStepProcessorTests
{
    private const string Config =
        """{"operationType":"database_touch","parameters":{"table":"orders"}}""";

    private static (WorkflowStepExecution Execution, RetryableOperationStepProcessor Processor) Create(string config)
    {
        var step = WorkflowStep.Create(Guid.NewGuid(), 8, nameof(StepType.RetryableOperation), config);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 8);
        typeof(WorkflowStepExecution)
            .GetProperty(nameof(WorkflowStepExecution.WorkflowStep))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(execution, new object[] { step });
        execution.Start();

        return (execution, new RetryableOperationStepProcessor(NullLogger<RetryableOperationStepProcessor>.Instance));
    }

    private static async Task<bool> TryProcessOneAsync(string config)
    {
        var (execution, processor) = Create(config);
        return await processor.ProcessAsync(execution, null);
    }

    [Fact]
    public async Task Operation_succeeds_on_first_or_early_try()
    {
        bool succeeded = false;
        for (int i = 0; i < 10 && !succeeded; i++)
            succeeded = await TryProcessOneAsync(Config);

        Assert.True(succeeded, "database_touch succeeded within 10 attempts");
    }

    [Fact]
    public async Task Unreliable_operation_eventually_surfaces_a_failure()
    {
        bool failed = false;
        for (int i = 0; i < 30 && !failed; i++)
            failed = !await TryProcessOneAsync("""{"operationType":"unreliable_api_call","parameters":{}}""");

        Assert.True(failed, "unreliable_api_call produced a failure within 30 attempts");
    }

    [Fact]
    public async Task Missing_parameters_throws()
    {
        var (execution, processor) = Create("""{"operationType":"database_touch"}""");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => processor.ProcessAsync(execution, null));
    }
}