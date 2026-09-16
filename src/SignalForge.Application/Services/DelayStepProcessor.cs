using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services
{
    public class DelayStepProcessor : IStepProcessor
    {
        private readonly ILogger<DelayStepProcessor> _logger;

        public string StepTypeKey => nameof(StepType.Delay);

        public DelayStepProcessor(ILogger<DelayStepProcessor> logger)
        {
            _logger = logger;
        }

        public async Task<bool> ProcessAsync(
            WorkflowStepExecution stepExecution,
            StepExecutionContext? context,
            CancellationToken cancellationToken = default)
        {
            // Parse configuration
            var config = JsonDocument.Parse(stepExecution.WorkflowStep.Configuration).RootElement;

            var delaySeconds = config.GetProperty("seconds").GetInt32();

            _logger.LogInformation("Delaying step {StepId} for {Seconds} seconds", stepExecution.Id, delaySeconds);

            try
            {
                // Delay for the specified time
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);

                _logger.LogInformation("Delay step {StepId} completed", stepExecution.Id);

                // Mark as succeeded
                stepExecution.Succeed($"Delayed for {delaySeconds} seconds");
                return true; // Continue to next step
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("Delay step {StepId} was cancelled", stepExecution.Id);
                stepExecution.Cancel();
                return false; // Don't retry cancelled steps
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delay step {StepId} failed", stepExecution.Id);
                stepExecution.Fail($"Delay step failed: {ex.Message}");
                return false; // Will trigger retry logic
            }
        }
    }
}