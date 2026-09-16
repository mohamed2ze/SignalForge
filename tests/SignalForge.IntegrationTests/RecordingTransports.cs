using SignalForge.Application.Notifications;

namespace SignalForge.IntegrationTests;

/// <summary>
/// In-memory email/SMS transports for integration tests: record the message and return a
/// deterministic delivery id without touching a real mail server or gateway.
/// </summary>
public sealed class RecordingEmailTransport : IOutboundEmailTransport
{
    public string? LastTo { get; private set; }
    public string? LastSubject { get; private set; }
    public string? LastBody { get; private set; }
    public int SendCount { get; private set; }

    public Task<string> SendAsync(string to, string? subject, string body, CancellationToken cancellationToken = default)
    {
        LastTo = to;
        LastSubject = subject;
        LastBody = body;
        SendCount++;
        return Task.FromResult($"email-{SendCount}");
    }
}

public sealed class RecordingSmsTransport : IOutboundSmsTransport
{
    public string? LastTo { get; private set; }
    public string? LastBody { get; private set; }
    public int SendCount { get; private set; }

    public Task<string> SendAsync(string to, string body, CancellationToken cancellationToken = default)
    {
        LastTo = to;
        LastBody = body;
        SendCount++;
        return Task.FromResult($"sms-{SendCount}");
    }
}