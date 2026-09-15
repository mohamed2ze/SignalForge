using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SignalForge.Application.Broker;
using SignalForge.Infrastructure.Broker;

namespace SignalForge.Worker;

/// <summary>
/// DI wiring for the message broker transport. <c>Broker:Provider</c> selects the backend:
/// <c>InMemory</c> (default, ephemeral, for local dev/tests) or <c>Sql</c> (durable — appended to the
/// shared SQL database so published messages survive worker restarts). The outbox sender is
/// scoped, so a scoped broker instance is correct for both backends.
/// Callers must register an <see cref="ISignalForgeDbContext"/> scoped service beforehand so the
/// <c>Sql</c> backend can resolve its store.
/// </summary>
public static class MessageBrokerRegistration
{
    public static IServiceCollection AddMessageBroker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var brokerProvider = configuration.GetValue<string>("Broker:Provider") ?? "InMemory";

        if (brokerProvider.Equals("Sql", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<SqlMessageBroker>();
            services.AddScoped<IMessageBroker>(sp => sp.GetRequiredService<SqlMessageBroker>());
            services.AddScoped<IBrokerAudit>(sp => sp.GetRequiredService<SqlMessageBroker>());
        }
        else
        {
            services.AddSingleton<InMemoryMessageBroker>();
            services.AddSingleton<IMessageBroker>(sp => sp.GetRequiredService<InMemoryMessageBroker>());
            services.AddSingleton<IBrokerAudit>(sp => sp.GetRequiredService<InMemoryMessageBroker>());
        }

        return services;
    }
}