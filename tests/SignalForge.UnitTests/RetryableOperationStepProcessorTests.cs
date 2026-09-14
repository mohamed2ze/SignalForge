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

    // ---------- explicit failureRate (L7) ----------

    [Fact]
    public async Task FailureRate_Of_One_Always_Fails()
    {
        for (var i = 0; i < 20; i++)
        {
            var (execution, processor) = Create(
                """{"operationType":"unreliable_api_call","parameters":{"failureRate":1}}""");
            var succeeded = await processor.ProcessAsync(execution, null);

            Assert.False(succeeded);
            Assert.Equal("Failed", execution.Status);
        }
    }

    [Fact]
    public async Task FailureRate_Of_Zero_Always_Succeeds()
    {
        for (var i = 0; i < 20; i++)
        {
            var (execution, processor) = Create(
                """{"operationType":"unreliable_api_call","parameters":{"failureRate":0}}""");
            var succeeded = await processor.ProcessAsync(execution, null);

            Assert.True(succeeded);
            Assert.Equal("Succeeded", execution.Status);
        }
    }

    [Fact]
    public async Task FailureRate_Out_Of_Range_Is_Clamped()
    {
        var (execution, processor) = Create(
            """{"operationType":"database_touch","parameters":{"failureRate":2}}""");
        // Clamped to 1.0 -> guaranteed failure rather than a probability above 1.
        Assert.False(await processor.ProcessAsync(execution, null));
    }

    [Fact]
    public async Task Absent_FailureRate_Keeps_BuiltIn_Defaults()
    {
        // unreliable_api_call historically failed ~30% of the time; with the default intact it
        // must still eventually produce both outcomes, never a guaranteed success/failure.
        bool sawSuccess = false, sawFailure = false;
        for (var i = 0; i < 40 && (!sawSuccess || !sawFailure); i++)
        {
            var succeeded = await TryProcessOneAsync(
                """{"operationType":"unreliable_api_call","parameters":{}}""");
            sawSuccess |= succeeded;
            sawFailure |= !succeeded;
        }

        Assert.True(sawSuccess, "default unreliable_api_call produced a success within 40 attempts");
        Assert.True(sawFailure, "default unreliable_api_call produced a failure within 40 attempts");
    }
}