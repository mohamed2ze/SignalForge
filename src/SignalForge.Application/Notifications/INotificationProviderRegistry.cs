using System.Collections.Generic;

namespace SignalForge.Application.Notifications;

/// <summary>
/// Resolves notification providers by their configured provider type key.
/// </summary>
public interface INotificationProviderRegistry
{
    /// <summary>
    /// Resolves a provider by type, case-insensitively. Returns null when the provider type is not
    /// registered — callers must fail loudly rather than silently skip delivery.
    /// </summary>
    INotificationProvider? Get(string providerType);

    /// <summary>The registered provider type keys, sorted.</summary>
    IReadOnlyCollection<string> AvailableProviders { get; }
}