using SignalForge.Application.Notifications;

namespace SignalForge.UnitTests;

/// <summary>
/// In-memory email transport for tests: records the message it was asked to send and returns a
/// deterministic delivery id. Never throws unless configured to.
/// </summary>
public sealed class RecordingEmailTransport : IOutboundEmailTransport
{
    public string? LastTo { get; private set; }
    public string? LastSubject { get; private set; }
    public string? LastBody { get; private set; }
    public int SendCount { get; private set; }
    public Exception? ThrowOnNext { get; set; }

    public Task<string> SendAsync(string to, string? subject, string body, CancellationToken cancellationToken = default)
    {
        if (ThrowOnNext is not null)
        {
            var error = ThrowOnNext;
            ThrowOnNext = null;
            throw error;
        }

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
    public Exception? ThrowOnNext { get; set; }

    public Task<string> SendAsync(string to, string body, CancellationToken cancellationToken = default)
    {
        if (ThrowOnNext is not null)
        {
            var error = ThrowOnNext;
            ThrowOnNext = null;
            throw error;
        }

        LastTo = to;
        LastBody = body;
        SendCount++;
        return Task.FromResult($"sms-{SendCount}");
    }
}