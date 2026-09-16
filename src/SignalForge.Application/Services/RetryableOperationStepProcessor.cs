using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services;

/// <summary>
/// Dispatches RetryableOperation steps to a real, registered <see cref="IRetryableOperation"/>
/// resolved via <see cref="IRetryableOperationRegistry"/>. The operationType in the step
/// configuration selects the implementation; an unregistered type is a deterministic failure
/// (schedules a retry), never a random outcome.
/// </summary>
public class RetryableOperationStepProcessor : IStepProcessor
{
    private readonly IRetryableOperationRegistry _operations;
    private readonly ILogger<RetryableOperationStepProcessor> _logger;

    public string StepTypeKey => nameof(StepType.RetryableOperation);

    public RetryableOperationStepProcessor(
        IRetryableOperationRegistry operations,
        ILogger<RetryableOperationStepProcessor> logger)
    {
        _operations = operations;
        _logger = logger;
    }

    public async Task<bool> ProcessAsync(
        WorkflowStepExecution stepExecution,
        StepExecutionContext? context,
        CancellationToken cancellationToken = default)
    {
        var config = JsonDocument.Parse(stepExecution.WorkflowStep.Configuration).RootElement;

        var operationType = config.GetProperty("operationType").GetString()!;
        var parametersJson = config.TryGetProperty("parameters", out var parameters)
            ? parameters.GetRawText()
            : "{}";

        _logger.LogInformation("Executing retryable operation step {StepId}: {OperationType}",
            stepExecution.Id, operationType);

        var operation = _operations.Get(operationType);
        if (operation is null)
        {
            var errorMsg = $"No retryable operation registered for '{operationType}'";
            _logger.LogWarning("Retryable operation step {StepId} {ErrorMsg}", stepExecution.Id, errorMsg);
            stepExecution.Fail(errorMsg);
            return false;
        }

        try
        {
            var result = await operation.ExecuteAsync(parametersJson, cancellationToken);

            if (result.Succeeded)
            {
                _logger.LogInformation("Retryable operation step {StepId} succeeded", stepExecution.Id);
                stepExecution.Succeed(result.Output ?? $"Operation {operationType} completed successfully");
                return true;
            }

            var failureMsg = result.ErrorMessage ?? $"Retryable operation {operationType} failed";
            _logger.LogWarning("Retryable operation step {StepId} {ErrorMsg}", stepExecution.Id, failureMsg);
            stepExecution.Fail(failureMsg);
            return false;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Retryable operation step {StepId} was cancelled", stepExecution.Id);
            stepExecution.Cancel();
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retryable operation step {StepId} failed", stepExecution.Id);
            stepExecution.Fail($"Retryable operation failed: {ex.Message}");
            return false;
        }
    }
}