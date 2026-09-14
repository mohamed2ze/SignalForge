using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SignalForge.Application.ExpressionEvaluation;
using SignalForge.Application.Services;
using SignalForge.Domain.Enums;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services
{
    /// <summary>
    /// Processes Conditional/rule workflow steps.
    /// Evaluates a condition and determines the next step based on the result.
    /// </summary>
    public class ConditionalStepProcessor : IStepProcessor
    {
        private readonly ILogger<ConditionalStepProcessor> _logger;

        public string StepTypeKey => nameof(StepType.Conditional);

        public ConditionalStepProcessor(ILogger<ConditionalStepProcessor> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<bool> ProcessAsync(
            WorkflowStepExecution stepExecution,
            StepExecutionContext? context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Parse configuration
                var config = JsonDocument.Parse(stepExecution.WorkflowStep.Configuration).RootElement;

                var conditionExpression = config.GetProperty("expression").GetString()!;
                var trueStepNumber = config.GetProperty("trueStep").GetInt32();
                var falseStepNumber = config.GetProperty("falseStep").GetInt32();

                _logger.LogInformation("Evaluating condition for step {StepId}: {Expression}",
                    stepExecution.Id, conditionExpression);
                // Evaluate the condition expression against the execution context.
                // Errors never throw: they log and safely evaluate to FALSE.
                var result = ConditionExpressionEvaluator.Evaluate(conditionExpression, context?.Root);

                if (result.Error != null)
                {
                    _logger.LogWarning(
                        "Condition expression error for step {StepId}: {Error} — evaluating as FALSE",
                        stepExecution.Id, result.Error);
                }

                int routeToStepNumber = result.Value ? trueStepNumber : falseStepNumber;

                if (result.Value)
                {
                    _logger.LogInformation("Condition evaluated to TRUE for step {StepId}. Proceeding to step {TrueStep}",
                        stepExecution.Id, trueStepNumber);

                    stepExecution.Succeed(
                        $"Condition evaluated to TRUE. Next step: {trueStepNumber}",
                        routeToStepNumber);
                }
                else
                {
                    _logger.LogInformation("Condition evaluated to FALSE for step {StepId}. Proceeding to step {FalseStep}",
                        stepExecution.Id, falseStepNumber);

                    stepExecution.Succeed(
                        $"Condition evaluated to FALSE. Next step: {falseStepNumber}",
                        routeToStepNumber);
                }

                // The orchestrator routes to RouteToStepNumber (then/else) when set,
                // or falls back to the sequential next step otherwise.
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating condition for step {StepId}", stepExecution.Id);
                stepExecution.Fail($"Condition evaluation failed: {ex.Message}");
                return false; // Will trigger retry logic
            }
        }
    }
}