using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prometheus;
using SignalForge.Application.Data;
using SignalForge.Domain.Models;

namespace SignalForge.Api.Observability;

/// <summary>
/// Keeps Prometheus gauges that reflect the shared-database backlog up to date. The scraper pulls
/// <c>/metrics</c> from the API, so a single scrape endpoint exposes both HTTP traffic counters and
/// the durable queues the workers drain (outbox, active executions). Refresh interval is readable
/// in seconds via <c>Metrics:RefreshSeconds</c>.
/// </summary>
public sealed class BacklogMetricsHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<MetricsOptions> _options;
    private readonly ILogger<BacklogMetricsHostedService> _logger;

    private static readonly Gauge OutboxPending = Metrics.CreateGauge(
        "signalforge_outbox_pending",
        "Outbox messages not yet processed (the durable send queue depth).");

    private static readonly Gauge ExecutionsActive = Metrics.CreateGauge(
        "signalforge_executions_active",
        "Workflow executions still Pending or Running (not yet terminal).");

    private static readonly Gauge DeadLetterBacklog = Metrics.CreateGauge(
        "signalforge_deadletter_backlog",
        "Dead-letter messages awaiting operator replay/cleanup.");

    public BacklogMetricsHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<MetricsOptions> options,
        ILogger<BacklogMetricsHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, _options.Value.RefreshSeconds));

        using var timer = new PeriodicTimer(period);
        await RefreshAsync(stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RefreshAsync(stoppingToken);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ISignalForgeDbContext>();

            OutboxPending.Set(await db.OutboxMessages.CountAsync(m => !m.IsProcessed, cancellationToken));
            ExecutionsActive.Set(await db.WorkflowExecutions.CountAsync(
                e => e.Status == WorkflowExecutionStatus.Pending
                     || e.Status == WorkflowExecutionStatus.Running,
                cancellationToken));
            DeadLetterBacklog.Set(await db.DeadLetterMessages.CountAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown: stop refreshing, do not log an error.
        }
        catch (Exception ex)
        {
            // A failed refresh is transient (DB briefly unreachable); keep the last good values.
            _logger.LogWarning("Metrics backlog refresh failed: {Message}", ex.Message);
        }
    }
}