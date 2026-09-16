namespace SignalForge.Application.Notifications;

/// <summary>
/// Configuration for the SMTP email transport (config section: "Notifications:Smtp"). The
/// provider fails closed: when <see cref="Host"/> is not configured the transport throws rather
/// than silently dropping email.
/// </summary>
public class SmtpNotificationOptions
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? FromAddress { get; set; }

    public string? FromDisplayName { get; set; }

    public bool EnableSsl { get; set; } = true;

    /// <summary>Per-message connect timeout before the transport fails.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}