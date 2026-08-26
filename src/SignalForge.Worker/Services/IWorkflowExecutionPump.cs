using System.Threading;
using System.Threading.Tasks;

namespace SignalForge.Worker.Services;

/// <summary>
/// Advances runnable workflow executions one step per poll cycle until they reach a terminal
/// state. Test-friendly like <see cref="IOutboxProcessor"/>: returns the next poll delay instead
/// of sleeping, so the pump's selection and pacing are observable without <see cref="Task.Delay"/>.
/// </summary>
public interface IWorkflowExecutionPump
{
    Task<TimeSpan> ProcessCycleAsync(CancellationToken cancellationToken = default);
}