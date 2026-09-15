using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalForge.Worker.Services;

namespace SignalForge.Worker.Services;

/// <summary>
/// Hosted loop that paces the workflow execution pump. Mirrors <see cref="Worker"/>'s outbox loop:
/// the pump itself never sleeps (it returns the next delay), so this service is the only place
/// that owns <see cref="Task.Delay"/> for execution advancement. On host shutdown it stops
/// accepting new cycles immediately but lets an in-flight cycle finish its current work up to
/// <see cref="HostingOptions.GracefulShutdownTimeoutSeconds"/> before hard-cancelling.
/// </summary>
public class WorkflowExecutionPumpHostedService(
    IServiceProvider serviceProvider,
    IOptions<HostingOptions> options,
    ILogger<WorkflowExecutionPumpHostedService> logger) : BackgroundService
{
    private readonly TimeSpan _drainTimeout =
        TimeSpan.FromSeconds(options.Value.GracefulShutdownTimeoutSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker starting workflow execution pump");

        var pump = serviceProvider.GetRequiredService<IWorkflowExecutionPump>();

        // Same graceful-drain contract as the outbox loop (see Worker): the work token is
        // decoupled from the host token so an in-flight advance is not interrupted mid-step.
        using var drainCts = new CancellationTokenSource();
        var drainToken = drainCts.Token;

        while (!stoppingToken.IsCancellationRequested)
        {
            drainCts.CancelAfter(_drainTimeout);

            TimeSpan nextDelay;
            try
            {
                nextDelay = await pump.ProcessCycleAsync(drainToken);
            }
            catch (OperationCanceledException)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                continue;
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