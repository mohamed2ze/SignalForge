using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalForge.Worker.Services;

namespace SignalForge.Worker;

/// <summary>
/// Outbox processing loop. Paces the outbox processor and owns the only <see cref="Task.Delay"/>
/// in the poll loop. On host shutdown it stops accepting new cycles immediately but gives an
/// in-flight cycle a bounded drain window to finish its current work (see
/// <see cref="HostingOptions.GracefulShutdownTimeoutSeconds"/>), so a graceful stop does not
/// interrupt an in-progress send mid-request.
/// </summary>
public class Worker(
    IServiceProvider serviceProvider,
    IOptions<HostingOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    private readonly TimeSpan _drainTimeout =
        TimeSpan.FromSeconds(options.Value.GracefulShutdownTimeoutSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker starting outbox processing");

        var processor = serviceProvider.GetRequiredService<IOutboxProcessor>();

        // Graceful drain: the work token is decoupled from the host's stop token. The host token
        // only gates *starting* new cycles; a cycle already in flight runs to completion (or up
        // to the drain window if it is genuinely stuck) rather than being cancelled the instant
        // shutdown begins.
        using var drainCts = new CancellationTokenSource();
        var drainToken = drainCts.Token;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Re-arm the drain window for each cycle. If a single cycle outlives it (stuck
            // consumer), the cycle is skipped rather than wedging the loop open; the next cycle
            // re-arms and continues.
            drainCts.CancelAfter(_drainTimeout);

            TimeSpan nextDelay;
            try
            {
                nextDelay = await processor.ProcessBatchAsync(drainToken);
            }
            catch (OperationCanceledException)
            {
                // Either the host stopped (new cycles are done) or the drain window fired on a
                // stuck cycle. Re-check the gate: if the host is still running, skip the whole
                // cycle and re-arm the drain window on the next iteration.
                if (stoppingToken.IsCancellationRequested)
                    break;

                continue;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in outbox poll cycle");
                nextDelay = TimeSpan.FromSeconds(10);
            }

            try
            {
                // Inter-cycle pacing honors the host token: once shutdown begins we stop
                // immediately after the in-flight cycle drains instead of scheduling another.
                await Task.Delay(nextDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Worker stopped");
    }
}