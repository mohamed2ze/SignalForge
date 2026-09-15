using Microsoft.Extensions.Logging.Abstractions;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.UnitTests;

public class RetryableOperationStepProcessorTests
{
    private sealed class FailOnDemandOperation : IRetryableOperation
    {
        public string OperationType => "fail_on_demand";
        public bool Fail { get; set; }
        public int Invocations { get; private set; }

        public Task<RetryableOperationResult> ExecuteAsync(string parametersJson, CancellationToken cancellationToken = default)
        {
            Invocations++;
            return Task.FromResult(Fail
                ? new RetryableOperationResult(false, "boom")
                : new RetryableOperationResult(true, Output: "done"));
        }
    }

    private sealed class ThrowingOperation : IRetryableOperation
    {
        public string OperationType => "throws";
        public Task<RetryableOperationResult> ExecuteAsync(string parametersJson, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("service unavailable");
    }

    private sealed class CalledOnDemandOperation : IRetryableOperation
    {
        public string OperationType => "echo";
        public string? LastParameters { get; private set; }
        public Task<RetryableOperationResult> ExecuteAsync(string parametersJson, CancellationToken cancellationToken = default)
        {
            LastParameters = parametersJson;
            return Task.FromResult(new RetryableOperationResult(true, Output: parametersJson));
        }
    }

    private static (WorkflowStepExecution Execution, RetryableOperationStepProcessor Processor, RetryableOperationRegistry Registry)
        Create(string config, params IRetryableOperation[] operations)
    {
        var step = WorkflowStep.Create(Guid.NewGuid(), 8, nameof(StepType.RetryableOperation), config);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 8);
        typeof(WorkflowStepExecution)
            .GetProperty(nameof(WorkflowStepExecution.WorkflowStep))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(execution, new object[] { step });
        execution.Start();

        var registry = new RetryableOperationRegistry(operations);
        var processor = new RetryableOperationStepProcessor(registry, NullLogger<RetryableOperationStepProcessor>.Instance);
        return (execution, processor, registry);
    }

    [Fact]
    public async Task Success_returning_operation_succeeds_the_step()
    {
        var operation = new CalledOnDemandOperation();
        var (execution, processor, _) = Create(
            """{"operationType":"echo","parameters":{"table":"orders"}}""", operation);

        var advance = await processor.ProcessAsync(execution, null);

        Assert.True(advance);
        Assert.Equal("Succeeded", execution.Status);
        Assert.NotNull(execution.Output);
        Assert.Contains("orders", execution.Output);
        Assert.Equal("""{"table":"orders"}""", operation.LastParameters);
    }

    [Fact]
    public async Task Missing_operation_registration_fails_deterministically()
    {
        var (execution, processor, _) = Create("""{"operationType":"database_touch"}""");

        var advance = await processor.ProcessAsync(execution, null);

        Assert.False(advance);
        Assert.Equal("Failed", execution.Status);
        Assert.Contains("database_touch", execution.ErrorMessage);
    }

    [Fact]
    public async Task Failing_operation_marks_step_failed_for_retry()
    {
        var operation = new FailOnDemandOperation { Fail = true };
        var (execution, processor, _) = Create(
            """{"operationType":"fail_on_demand","parameters":{}}""", operation);

        var advance = await processor.ProcessAsync(execution, null);

        Assert.False(advance);
        Assert.Equal("Failed", execution.Status);
        Assert.Contains("boom", execution.ErrorMessage);
    }

    [Fact]
    public async Task Operation_that_throws_marks_step_failed_for_retry()
    {
        var operation = new ThrowingOperation();
        var (execution, processor, _) = Create(
            """{"operationType":"throws","parameters":{}}""", operation);

        var advance = await processor.ProcessAsync(execution, null);

        Assert.False(advance);
        Assert.Equal("Failed", execution.Status);
        Assert.Contains("service unavailable", execution.ErrorMessage);
    }

    [Fact]
    public async Task Failed_operation_can_recover_on_a_later_attempt()
    {
        var operation = new FailOnDemandOperation { Fail = true };
        var (execution, processor, _) = Create(
            """{"operationType":"fail_on_demand","parameters":{}}""", operation);

        var first = await processor.ProcessAsync(execution, null);
        Assert.False(first);
        Assert.Equal("Failed", execution.Status);

        // A later attempt (deterministic: operation now succeeds) recovers the SAME record,
        // mirroring the orchestrator's PrepareForRetry + Start re-entry sequence.
        operation.Fail = false;
        execution.PrepareForRetry();
        execution.Start();
        var second = await processor.ProcessAsync(execution, null);

        Assert.True(second);
        Assert.Equal("Succeeded", execution.Status);
        Assert.Equal("done", execution.Output);
    }

    [Fact]
    public async Task Registry_is_case_insensitive_and_rejects_duplicates()
    {
        var echo = new EchoRetryableOperation();
        Assert.Same(echo, new RetryableOperationRegistry([echo]).Get("ECHO"));

        Assert.Throws<InvalidOperationException>(() => new RetryableOperationRegistry(
        [
            new CalledOnDemandOperation { },
            new CalledOnDemandOperation { }
        ]));

        Assert.Null(new RetryableOperationRegistry([]).Get("does_not_exist"));
    }

    [Fact]
    public async Task Config_without_parameters_defaults_to_empty_object()
    {
        var operation = new CalledOnDemandOperation();
        var (execution, processor, _) = Create("""{"operationType":"echo"}""", operation);

        var advance = await processor.ProcessAsync(execution, null);

        Assert.True(advance);
        Assert.Equal("Succeeded", execution.Status);
        Assert.Equal("{}", operation.LastParameters);
    }
}