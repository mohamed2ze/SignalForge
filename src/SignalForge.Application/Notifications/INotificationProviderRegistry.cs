using System.Collections.Generic;

namespace SignalForge.Application.Notifications;

public interface INotificationProviderRegistry
{
    /// <summary>
    /// Resolves a provider by type, case-insensitively. Returns null when the provider type is not
    /// registered — callers must fail loudly rather than silently skip delivery.
    /// </summary>
    INotificationProvider? Get(string providerType);

    IReadOnlyCollection<string> AvailableProviders { get; }
}