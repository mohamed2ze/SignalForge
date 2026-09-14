using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services
{
    /// <summary>
    /// Processes Retryable Operation workflow steps.
    /// Wraps an operation with retry logic.
    /// </summary>
    public class RetryableOperationStepProcessor : IStepProcessor
    {
        private readonly ILogger<RetryableOperationStepProcessor> _logger;

        public string StepTypeKey => nameof(StepType.RetryableOperation);

        public RetryableOperationStepProcessor(ILogger<RetryableOperationStepProcessor> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<bool> ProcessAsync(
            WorkflowStepExecution stepExecution,
            StepExecutionContext? context,
            CancellationToken cancellationToken = default)
        {
            // Parse configuration
            var config = JsonDocument.Parse(stepExecution.WorkflowStep.Configuration).RootElement;

            var operationType = config.GetProperty("operationType").GetString()!;
            var parametersJson = config.GetProperty("parameters").GetRawText();

            _logger.LogInformation("Executing retryable operation step {StepId}: {OperationType}",
                stepExecution.Id, operationType);

            try
            {
                // For demonstration purposes, we'll simulate different operation types
                // In a real implementation, this would call actual services or perform actual operations
                bool success = await SimulateOperationAsync(operationType, parametersJson, cancellationToken);

                if (success)
                {
                    _logger.LogInformation("Retryable operation step {StepId} succeeded", stepExecution.Id);
                    stepExecution.Succeed($"Operation {operationType} completed successfully");
                    return true; // Continue to next step
                }
                else
                {
                    var errorMsg = $"Retryable operation {operationType} failed";
                    _logger.LogWarning("Retryable operation step {StepId} {ErrorMsg}", stepExecution.Id, errorMsg);
                    stepExecution.Fail(errorMsg);
                    return false; // Will trigger retry logic
                }
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("Retryable operation step {StepId} was cancelled", stepExecution.Id);
                stepExecution.Cancel();
                return false; // Don't retry cancelled steps
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retryable operation step {StepId} failed", stepExecution.Id);
                stepExecution.Fail($"Retryable operation failed: {ex.Message}");
                return false; // Will trigger retry logic
            }
        }

        private static async Task<bool> SimulateOperationAsync(string operationType, string parametersJson, CancellationToken cancellationToken)
        {
            // Simulate operation processing time
            await Task.Delay(200, cancellationToken);

            return !SimulateFailure(operationType, parametersJson);
        }

        /// <summary>
        /// Draws a deterministic pass/fail outcome for the demo operation. When the step parameters
        /// carry an explicit <c>failureRate</c> (0..1), that probability is used verbatim
        /// (clamped); otherwise the built-in per-operation defaults apply.
        /// </summary>
        private static bool SimulateFailure(string operationType, string parametersJson)
        {
            var failureRate = ReadFailureRate(parametersJson);
            if (failureRate is not null)
                return Random.Shared.NextDouble() < Math.Clamp(failureRate.Value, 0d, 1d);

            // Simulate that some operations are inherently unreliable.
            if (operationType.Equals("unreliable_api_call", StringComparison.OrdinalIgnoreCase))
                return Random.Shared.NextDouble() < 0.3;
            if (operationType.Equals("external_service", StringComparison.OrdinalIgnoreCase))
                return Random.Shared.NextDouble() < 0.4;
            return Random.Shared.NextDouble() < 0.1;
        }

        private static double? ReadFailureRate(string parametersJson)
        {
            if (string.IsNullOrWhiteSpace(parametersJson))
                return null;

            try
            {
                using var document = JsonDocument.Parse(parametersJson);
                if (!document.RootElement.TryGetProperty("failureRate", out var failureRate))
                    return null;

                if (failureRate.ValueKind == JsonValueKind.Number)
                    return failureRate.GetDouble();
                if (failureRate.ValueKind == JsonValueKind.String && double.TryParse(
                    failureRate.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
                    return rate;

                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}