using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services
{
    public class EventEmissionStepProcessor : IStepProcessor
    {
        private readonly IEventIngestionService _eventIngestionService;
        private readonly ILogger<EventEmissionStepProcessor> _logger;

        public string StepTypeKey => nameof(StepType.EventEmission);

        public EventEmissionStepProcessor(
            IEventIngestionService eventIngestionService,
            ILogger<EventEmissionStepProcessor> logger)
        {
            _eventIngestionService = eventIngestionService;
            _logger = logger;
        }

        public async Task<bool> ProcessAsync(
            WorkflowStepExecution stepExecution,
            StepExecutionContext? context,
            CancellationToken cancellationToken = default)
        {
            // Parse configuration
            var config = JsonDocument.Parse(stepExecution.WorkflowStep.Configuration).RootElement;

            var eventType = config.GetProperty("eventType").GetString()!;
            var payloadJson = config.GetProperty("payload").GetRawText();

            _logger.LogInformation("Emitting event for step {StepId}: {EventType}", stepExecution.Id, eventType);

            try
            {
                // Resolve tenant from the execution context
                // The WorkflowExecution navigation is populated by EF fix-up when processed inside
                // WorkflowExecutionOrchestratorService (which loads the parent execution entity in
                // the same tracked DbContext).
                if (stepExecution.WorkflowExecution is null)
                    throw new InvalidOperationException(
                        $"Event emission step requires loaded workflow execution context for tenant resolution.");

                var tenantId = stepExecution.WorkflowExecution.TenantId;
                await _eventIngestionService.PublishAsync(tenantId, eventType, payloadJson, cancellationToken);

                _logger.LogInformation("Event emitted successfully for step {StepId}", stepExecution.Id);

                stepExecution.Succeed($"Event emitted: {eventType}");
                return true; // Continue to next step
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Event emission step {StepId} failed", stepExecution.Id);
                stepExecution.Fail($"Event emission failed: {ex.Message}");
                return false; // Will trigger retry logic
            }
        }
    }
}