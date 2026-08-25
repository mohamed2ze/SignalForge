using System;
using System.Collections.Generic;
using System.Linq;

namespace SignalForge.Application.Notifications;

/// <summary>
/// In-memory registry of notification providers. Provider types are normalized to lowercase and
/// compared case-insensitively. A null/Duplicate provider key surfaces as an argument error.
/// </summary>
public class NotificationProviderRegistry : INotificationProviderRegistry
{
    private readonly IReadOnlyDictionary<string, INotificationProvider> _providers;

    public NotificationProviderRegistry(IEnumerable<INotificationProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var byType = new Dictionary<string, INotificationProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!byType.TryAdd(provider.ProviderType, provider))
                throw new InvalidOperationException(
                    $"Notification provider type '{provider.ProviderType}' registered more than once.");
        }

        _providers = byType;
    }

    public INotificationProvider? Get(string providerType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerType);
        return _providers.GetValueOrDefault(providerType.Trim());
    }

    public IReadOnlyCollection<string> AvailableProviders =>
        _providers.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
}