namespace SignalForge.Application.Notifications;

/// <summary>
/// Result of a notification delivery, including the provider-assigned delivery id used for
/// observability and downstream reconciliation.
/// </summary>
public sealed record NotificationDeliveryResult(
    string DeliveryId,
    string ProviderType,
    NotificationDeliveryStatus Status,
    DateTime SentAt,
    string? Detail = null);