using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalForge.Application.Data;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;

namespace SignalForge.Worker.Services;

/// <summary>
/// The workflow engine's production driver: without this nothing advances executions created by
/// <c>POST /workflows/{id}/execute</c> outside the test harness
/// (<see cref="IWorkflowExecutionOrchestratorService.AdvanceWorkflowExecutionAsync"/> had no
/// production caller). SignalForge is run by the Worker because the Worker owns the outbox loop
/// and structured logging already; the pump is the natural seat for a future distributed
/// scheduler.
/// </summary>
public class WorkflowExecutionPump : IWorkflowExecutionPump
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ExecutionPumpOptions _options;
    private readonly ILogger<WorkflowExecutionPump> _logger;

    public WorkflowExecutionPump(
        IServiceScopeFactory scopeFactory,
        IOptions<ExecutionPumpOptions> options,
        ILogger<WorkflowExecutionPump> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TimeSpan> ProcessCycleAsync(CancellationToken cancellationToken = default)
    {
        // Fresh scope per cycle (mirrors OutboxProcessor): a short-lived DbContext avoids a
        // long-lived context holding stale change tracking across a long-running host.
        using var scope = _scopeFactory.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IWorkflowExecutionOrchestratorService>();

        try
        {
            var candidates = await dbContext.WorkflowExecutions
                .Where(e => e.Status == WorkflowExecutionStatus.Running)
                // Only pick executions whose next step is not already in flight (Pending/Running):
                // a step must not be double-started by repeated polls. Failed/Retrying steps are
                // allowed through — the orchestrator re-enters them in place (retries on the same
                // record) or fails the execution once attempts are exhausted.
                .Where(e => !dbContext.WorkflowStepExecutions.Any(se =>
                    se.WorkflowExecutionId == e.Id &&
                    se.StepNumber == e.CurrentStepNumber + 1 &&
                    (se.Status == WorkflowStepExecutionStatus.Pending ||
                     se.Status == WorkflowStepExecutionStatus.Running)))
                .OrderBy(e => e.StartedAt)
                .Take(_options.BatchSize)
                .ToListAsync(cancellationToken);

            _logger.LogDebug("Polled {Count} runnable workflow executions", candidates.Count);

            if (candidates.Count == 0)
                return TimeSpan.FromSeconds(_options.PollIntervalSeconds);

            foreach (var candidate in candidates)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var progressed = await orchestrator.AdvanceWorkflowExecutionAsync(
                        candidate.Id, cancellationToken);

                    _logger.LogDebug(
                        "Advanced workflow execution {executionId}: progressed {progressed}",
                        candidate.Id, progressed);
                }
                catch (Exception ex)
                {
                    // One bad execution must not stall the rest of the batch.
                    _logger.LogError(ex,
                        "Error advancing workflow execution {executionId}", candidate.Id);
                }
            }

            return TimeSpan.FromSeconds(_options.PollIntervalSeconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in workflow execution pump cycle");
            return TimeSpan.FromSeconds(_options.PollIntervalSeconds);
        }
    }
}