using System.Threading;
using System.Threading.Tasks;
using SignalForge.Domain.Models;

namespace SignalForge.Application.Services
{
    /// <summary>
    /// Interface for processing workflow steps.
    /// Each step type should have its own implementation.
    /// </summary>
    public interface IStepProcessor
    {
        /// <summary>The step-type key this processor handles (e.g. <c>nameof(StepType.HttpWebhook)</c>).</summary>
        string StepTypeKey { get; }

        /// <summary>
        /// Processes a workflow step execution.
        /// </summary>
        /// <param name="stepExecution">The step execution to process</param>
        /// <param name="context">Runtime execution context (event payload + step outputs). May be null for processors that do not need it.</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if processing should continue to next step, false if waiting for retry</returns>
        Task<bool> ProcessAsync(
            WorkflowStepExecution stepExecution,
            StepExecutionContext? context,
            CancellationToken cancellationToken = default);
    }
}