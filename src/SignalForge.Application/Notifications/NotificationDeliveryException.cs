using System;

namespace SignalForge.Application.Notifications;

/// <summary>
/// Thrown by a notification provider when delivery fails (transport error, non-accepting peer).
/// <see cref="NotificationDeliveryResult.Failed"/> is not used for failures — providers throw this
/// exception so the step processor can map it onto a failed step execution.
/// </summary>
public class NotificationDeliveryException : Exception
{
    public NotificationDeliveryException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}