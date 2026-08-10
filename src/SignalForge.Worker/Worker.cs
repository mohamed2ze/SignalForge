using Microsoft.Extensions.DependencyInjection;
using SignalForge.Worker.Services;

namespace SignalForge.Worker;

public class Worker(IServiceProvider serviceProvider, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker starting outbox processing");

        var processor = serviceProvider.GetRequiredService<IOutboxProcessor>();

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan nextDelay;
            try
            {
                nextDelay = await processor.ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in outbox poll cycle");
                nextDelay = TimeSpan.FromSeconds(10);
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

        logger.LogInformation("Worker stopped");
    }
}