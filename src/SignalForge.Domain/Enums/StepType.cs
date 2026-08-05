namespace SignalForge.Domain.Enums;

/// <summary>
/// Enumeration of workflow step types.
/// </summary>
public enum StepType
{
    /// <summary>
    /// HTTP webhook step - makes an HTTP request to an external endpoint.
    /// </summary>
    HttpWebhook,

    /// <summary>
    /// Delay step - waits for a specified duration.
    /// </summary>
    Delay,

    /// <summary>
    /// Conditional/rule step - evaluates a condition and branches accordingly.
    /// </summary>
    Conditional,

    /// <summary>
    /// Log/audit step - logs information for auditing purposes.
    /// </summary>
    LogAudit,

    /// <summary>
    /// Notification simulation step - simulates sending a notification.
    /// </summary>
    NotificationSimulation,

    /// <summary>
    /// Event emission step - emits a new event.
    /// </summary>
    EventEmission,

    /// <summary>
    /// Retryable operation step - wraps an operation with retry logic.
    /// </summary>
    RetryableOperation
}