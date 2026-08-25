namespace SignalForge.Application.Notifications;

/// <summary>
/// Outcome of a provider delivery attempt.
/// </summary>
public enum NotificationDeliveryStatus
{
    /// <summary>The provider accepted the message for delivery.</summary>
    Accepted,
    /// <summary>The provider rejected the message.</summary>
    Failed
}