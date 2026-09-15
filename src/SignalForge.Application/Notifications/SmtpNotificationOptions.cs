namespace SignalForge.Application.Notifications;

/// <summary>
/// Configuration for the SMTP email transport (config section: "Notifications:Smtp"). The
/// provider fails closed: when <see cref="Host"/> is not configured the transport throws rather
/// than silently dropping email.
/// </summary>
public class SmtpNotificationOptions
{
    /// <summary>SMTP server hostname. Empty value means "not configured" and blocks delivery.</summary>
    public string? Host { get; set; }

    /// <summary>SMTP server port. Defaults to the well-known submission port 587.</summary>
    public int Port { get; set; } = 587;

    /// <summary>Optional username for authenticated SMTP.</summary>
    public string? Username { get; set; }

    /// <summary>Optional password for authenticated SMTP.</summary>
    public string? Password { get; set; }

    /// <summary>The From address stamped on outgoing mail.</summary>
    public string? FromAddress { get; set; }

    /// <summary>Optional display name rendered next to the From address.</summary>
    public string? FromDisplayName { get; set; }

    /// <summary>Connects over implicit TLS/SSL when true (the default).</summary>
    public bool EnableSsl { get; set; } = true;

    /// <summary>Per-message connect timeout before the transport fails.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}