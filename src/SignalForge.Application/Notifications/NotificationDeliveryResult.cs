namespace SignalForge.Application.Notifications;

public sealed record NotificationDeliveryResult(
    string DeliveryId,
    string ProviderType,
    NotificationDeliveryStatus Status,
    DateTime SentAt,
    string? Detail = null);