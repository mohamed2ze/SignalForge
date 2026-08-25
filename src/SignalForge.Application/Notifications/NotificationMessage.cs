namespace SignalForge.Application.Notifications;

/// <summary>
/// A notification to deliver through a provider. Carries the recipient routing information and
/// the message content; provider-specific routing (webhook URL etc.) is intentionally not part of
/// the message — providers are selected by <see cref="ProviderType"/> and receive the same message.
/// </summary>
public sealed record NotificationMessage(
    string ProviderType,
    string Recipient,
    string? Subject,
    string Body);