using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services
{
    public class LogAuditStepProcessor : IStepProcessor
    {
        private readonly ILogger<LogAuditStepProcessor> _logger;

        public string StepTypeKey => nameof(StepType.LogAudit);

        public LogAuditStepProcessor(ILogger<LogAuditStepProcessor> logger)
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

            var message = config.GetProperty("message").GetString()!;
            var logLevel = config.GetProperty("logLevel").GetString()?.ToLower() ?? "information";

            _logger.LogInformation("Processing log/audit step {StepId}", stepExecution.Id);

            try
            {
                // Log the audit message with the specified level
                switch (logLevel)
                {
                    case "trace":
                        _logger.LogTrace("AUDIT: {Message} | StepId: {StepId} | WorkflowExecutionId: {WorkflowExecutionId}",
                            message, stepExecution.Id, stepExecution.WorkflowExecutionId);
                        break;
                    case "debug":
                        _logger.LogDebug("AUDIT: {Message} | StepId: {StepId} | WorkflowExecutionId: {WorkflowExecutionId}",
                            message, stepExecution.Id, stepExecution.WorkflowExecutionId);
                        break;
                    case "warning":
                        _logger.LogWarning("AUDIT: {Message} | StepId: {StepId} | WorkflowExecutionId: {WorkflowExecutionId}",
                            message, stepExecution.Id, stepExecution.WorkflowExecutionId);
                        break;
                    case "error":
                        _logger.LogError("AUDIT: {Message} | StepId: {StepId} | WorkflowExecutionId: {WorkflowExecutionId}",
                            message, stepExecution.Id, stepExecution.WorkflowExecutionId);
                        break;
                    case "critical":
                        _logger.LogCritical("AUDIT: {Message} | StepId: {StepId} | WorkflowExecutionId: {WorkflowExecutionId}",
                            message, stepExecution.Id, stepExecution.WorkflowExecutionId);
                        break;
                    case "information":
                    default:
                        _logger.LogInformation("AUDIT: {Message} | StepId: {StepId} | WorkflowExecutionId: {WorkflowExecutionId}",
                            message, stepExecution.Id, stepExecution.WorkflowExecutionId);
                        break;
                }

                // Also store the audit message in the step output for potential retrieval
                stepExecution.Succeed($"Audit logged: {message}");
                return true; // Continue to next step
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Log/audit step {StepId} failed", stepExecution.Id);
                stepExecution.Fail($"Log/audit step failed: {ex.Message}");
                return false; // Will trigger retry logic
            }
        }
    }
}