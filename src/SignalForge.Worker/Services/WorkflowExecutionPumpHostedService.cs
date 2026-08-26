using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SignalForge.Worker.Services;

/// <summary>
/// Hosted loop that paces the workflow execution pump. Mirrors <see cref="Worker"/>'s outbox loop:
/// the pump itself never sleeps (it returns the next delay), so this service is the only place
/// that owns <see cref="Task.Delay"/> for execution advancement.
/// </summary>
public class WorkflowExecutionPumpHostedService(
    IServiceProvider serviceProvider,
    ILogger<WorkflowExecutionPumpHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker starting workflow execution pump");

        var pump = serviceProvider.GetRequiredService<IWorkflowExecutionPump>();

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan nextDelay;
            try
            {
                nextDelay = await pump.ProcessCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in workflow execution pump cycle");
                nextDelay = TimeSpan.FromSeconds(5);
            }

            try
            {
                await Task.Delay(nextDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Workflow execution pump stopped");
    }
}