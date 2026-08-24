using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SignalForge.Infrastructure.Data;

namespace SignalForge.Api.Health;

/// <summary>
/// Readiness probe: confirms the SQL Server database is reachable from the API host.
/// Bounded by a short timeout so an unhealthy database degrades the readiness endpoint
/// instead of hanging it (connection retry policies can otherwise swell the latency).
/// </summary>
public class DatabaseHealthCheck : IHealthCheck
{
    private const int ProbeTimeoutMilliseconds = 2000;

    private readonly IServiceScopeFactory _scopeFactory;

    public DatabaseHealthCheck(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SignalForgeDbContext>();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeoutMilliseconds);

            return await dbContext.Database.CanConnectAsync(timeout.Token)
                ? HealthCheckResult.Healthy("Database reachable")
                : HealthCheckResult.Unhealthy("Database not reachable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"Database probe timed out after {ProbeTimeoutMilliseconds}ms");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database probe failed", ex);
        }
    }
}