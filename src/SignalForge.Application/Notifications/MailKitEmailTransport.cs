using System;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace SignalForge.Application.Notifications;

/// <summary>
/// Real SMTP email transport backed by MailKit. Fails closed when the SMTP host is not
/// configured; authenticates when credentials are supplied. The message id assigned by the mail
/// server's SMTP transcript (queue id / Message-Id) is returned as the delivery id.
/// </summary>
public class MailKitEmailTransport : IOutboundEmailTransport
{
    private readonly SmtpNotificationOptions _options;
    private readonly ILogger<MailKitEmailTransport> _logger;

    public MailKitEmailTransport(
        IOptions<SmtpNotificationOptions> options,
        ILogger<MailKitEmailTransport> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> SendAsync(
        string to,
        string? subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Host))
            throw new NotificationDeliveryException(
                "SMTP delivery is not configured (Notifications:Smtp:Host is empty).");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromDisplayName, ResolveFromAddress()));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject ?? "(no subject)";
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();

        try
        {
            client.Timeout = _options.TimeoutSeconds * 1000;
            await client.ConnectAsync(_options.Host, _options.Port, _options.EnableSsl, cancellationToken);

            if (!string.IsNullOrEmpty(_options.Username) || !string.IsNullOrEmpty(_options.Password))
                await client.AuthenticateAsync(_options.Username ?? string.Empty, _options.Password ?? string.Empty, cancellationToken);

            var response = await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);

            return $"smtp:{message.MessageId}";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NotificationDeliveryException("SMTP transport timed out.");
        }
        catch (Exception ex) when (ex is not NotificationDeliveryException)
        {
            _logger.LogWarning(ex, "SMTP delivery to {Recipient} failed", to);
            throw new NotificationDeliveryException($"SMTP transport error: {ex.Message}", ex);
        }
    }

    private string ResolveFromAddress()
    {
        if (!string.IsNullOrWhiteSpace(_options.FromAddress))
            return _options.FromAddress;

        throw new NotificationDeliveryException(
            "SMTP delivery is not configured (Notifications:Smtp:FromAddress is empty).");
    }
}