using Microsoft.Extensions.Logging.Abstractions;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.UnitTests;

public class EventEmissionStepProcessorTests
{
    private const string Config =
        """{"eventType":"order.fulfilled","payload":{"a":1}}""";

    [Fact]
    public async Task Emits_event_to_ingestion_service()
    {
        var ingestion = new FakeEventIngestionService();
        var (execution, processor) = Create(Config, ingestion, withWorkflowExecution: true);

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.True(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Succeeded, execution.Status);
        Assert.Equal("Event emitted: order.fulfilled", execution.Output);
        Assert.Equal(ExpectedTenantId, ingestion.LastTenantId);
        Assert.Equal("order.fulfilled", ingestion.LastEventType);
        Assert.Equal("""{"a":1}""", ingestion.LastPayload);
    }

    [Fact]
    public async Task Missing_workflow_execution_fails_the_step()
    {
        var ingestion = new FakeEventIngestionService();
        var (execution, processor) = Create(Config, ingestion, withWorkflowExecution: false);

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.False(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Failed, execution.Status);
        Assert.Contains("requires loaded workflow execution context", execution.ErrorMessage);
        Assert.Null(ingestion.LastEventType);
    }

    [Fact]
    public async Task Ingestion_failure_fails_the_step()
    {
        var ingestion = new FakeEventIngestionService { Throw = true };
        var (execution, processor) = Create(Config, ingestion, withWorkflowExecution: true);

        bool shouldContinue = await processor.ProcessAsync(execution, null);

        Assert.False(shouldContinue);
        Assert.Equal(WorkflowStepExecutionStatus.Failed, execution.Status);
        Assert.Contains("Event emission failed", execution.ErrorMessage);
    }

    [Fact]
    public async Task Missing_event_type_throws()
    {
        var ingestion = new FakeEventIngestionService();
        var (execution, processor) = Create("""{"payload":{"a":1}}""", ingestion, withWorkflowExecution: true);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => processor.ProcessAsync(execution, null));
    }

    private static readonly Guid ExpectedTenantId = Guid.NewGuid();

    private static (WorkflowStepExecution Execution, EventEmissionStepProcessor Processor)
        Create(string config, FakeEventIngestionService ingestion, bool withWorkflowExecution)
    {
        var step = WorkflowStep.Create(Guid.NewGuid(), 7, nameof(StepType.EventEmission), config);
        var execution = WorkflowStepExecution.Create(Guid.NewGuid(), step.Id, 7);
        typeof(WorkflowStepExecution)
            .GetProperty(nameof(WorkflowStepExecution.WorkflowStep))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(execution, new object[] { step });

        if (withWorkflowExecution)
        {
            var workflowExecution = WorkflowExecution.Create(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ExpectedTenantId);
            typeof(WorkflowStepExecution)
                .GetProperty(nameof(WorkflowStepExecution.WorkflowExecution))!
                .GetSetMethod(nonPublic: true)!
                .Invoke(execution, new object[] { workflowExecution });
        }

        execution.Start();

        var processor = new EventEmissionStepProcessor(
            ingestion, NullLogger<EventEmissionStepProcessor>.Instance);

        return (execution, processor);
    }

    private sealed class FakeEventIngestionService : IEventIngestionService
    {
        public Guid? LastTenantId { get; private set; }
        public string? LastEventType { get; private set; }
        public string? LastPayload { get; private set; }
        public bool Throw { get; set; }

        public Task<IngestEventResult> IngestEventAsync(
            Guid tenantId, string externalEventId, string eventType, DateTime occurredAt, string payload,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new IngestEventResult
            {
                Event = Event.Create(tenantId, externalEventId, eventType, occurredAt, payload),
                IsNewEvent = true,
            });

        public Task PublishAsync(
            Guid tenantId, string eventType, string payload, CancellationToken cancellationToken = default)
        {
            if (Throw)
                throw new InvalidOperationException("outbox unavailable");

            LastTenantId = tenantId;
            LastEventType = eventType;
            LastPayload = payload;
            return Task.CompletedTask;
        }

        public Task<Event?> GetEventByIdAsync(Guid eventId, Guid tenantId) => Task.FromResult<Event?>(null);
    }
}