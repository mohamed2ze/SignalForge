using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Application.Services;

/// <summary>
/// Advances a workflow execution one step, dispatching each step to its processor through the
/// registry. This is the forward-progress half of execution orchestration (the "what runs next"
/// decision); lifecycle start/read/retry-eligibility stay on
/// <see cref="IWorkflowExecutionOrchestratorService"/>.
/// </summary>
public interface IWorkflowExecutionAdvancer
{
    Task<bool> AdvanceWorkflowExecutionAsync(Guid executionId, CancellationToken cancellationToken = default);
}