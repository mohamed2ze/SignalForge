using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalForge.Application.Notifications;
using SignalForge.Application.Security;

namespace SignalForge.UnitTests.Notifications;

/// <summary>Minimal ILogger that records formatted messages for inspection.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception));
}

internal sealed class SmsStubHandler : HttpMessageHandler
{
    private Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public SmsStubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastRequestBody = request.Content.ReadAsStringAsync(cancellationToken).Result;
        return Task.FromResult(_responder(request));
    }
}

public class HttpSmsTransportTests
{
    private static HttpSmsTransport CreateTransport(
        SmsNotificationOptions options,
        SmsStubHandler handler,
        OutboundWebhookOptions? outboundOptions = null)
    {
        return new HttpSmsTransport(
            new HttpClient(handler),
            Options.Create(options),
            Options.Create(outboundOptions ?? new OutboundWebhookOptions()),
            NullLogger<HttpSmsTransport>.Instance);
    }

    [Fact]
    public async Task Posts_json_body_with_api_key_header_and_returns_sid()
    {
        var handler = new SmsStubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"sid":"SM123","status":"queued"}""")
        });
        var transport = CreateTransport(new SmsNotificationOptions
        {
            ApiBaseUrl = "https://sms.example.test/v1/messages",
            ApiKey = "secret-gateway-key"
        }, handler);

        var deliveryId = await transport.SendAsync("+15551234567", "hello world");

        Assert.Equal("SM123", deliveryId);
        Assert.Equal("POST", handler.LastRequest!.Method.Method);
        Assert.Equal(new Uri("https://sms.example.test/v1/messages"), handler.LastRequest.RequestUri);
        Assert.Equal("secret-gateway-key",
            handler.LastRequest.Headers.GetValues("X-Api-Key").Single());
        var body = JsonNode.Parse(handler.LastRequestBody!)!;
        Assert.Equal("+15551234567", (string?)body["to"]);
        Assert.Equal("hello world", (string?)body["message"]);
    }

    [Fact]
    public async Task Non_2xx_response_throws_delivery_exception()
    {
        var handler = new SmsStubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)
        {
            Content = new StringContent("gateway down")
        });
        var transport = CreateTransport(new SmsNotificationOptions
        {
            ApiBaseUrl = "https://sms.example.test/v1/messages"
        }, handler);

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => transport.SendAsync("+15551234567", "hi"));

        Assert.Contains("502", ex.Message);
    }

    [Fact]
    public async Task Unconfigured_gateway_fails_closed_without_any_request()
    {
        var handler = new SmsStubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var transport = CreateTransport(new SmsNotificationOptions(), handler);

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => transport.SendAsync("+15551234567", "hi"));

        Assert.Contains("not configured", ex.Message);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Non_https_gateway_url_is_rejected_by_the_ssrf_guard()
    {
        var handler = new SmsStubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var transport = CreateTransport(new SmsNotificationOptions
        {
            ApiBaseUrl = "http://private.example/sms"
        }, handler);

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => transport.SendAsync("+15551234567", "hi"));

        Assert.NotNull(ex.Message);
        Assert.Null(handler.LastRequest);
    }
}

public class MailKitEmailTransportTests
{
    private static MailKitEmailTransport CreateTransport(SmtpNotificationOptions options)
        => new(Options.Create(options), NullLogger<MailKitEmailTransport>.Instance);

    [Fact]
    public async Task Unconfigured_host_fails_closed()
    {
        var transport = CreateTransport(new SmtpNotificationOptions());

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => transport.SendAsync("user@example.com", "Subject", "Body"));

        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public async Task Missing_from_address_fails_closed_even_with_a_host()
    {
        var transport = CreateTransport(new SmtpNotificationOptions { Host = "smtp.example.test" });

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => transport.SendAsync("user@example.com", "Subject", "Body"));

        Assert.Contains("FromAddress", ex.Message);
    }
}

public class NotificationProviderContentLoggingTests
{
    private const string SecretSubject = "TOP-SECRET-PAYLOAD-SUBJECT-9f2";
    private const string SecretBody = "TOP-SECRET-PAYLOAD-BODY-7c1";

    [Fact]
    public async Task Email_provider_delegates_and_never_logs_content()
    {
        var transport = new RecordingEmailTransport();
        var provider = new EmailNotificationProvider(transport);

        var result = await provider.SendAsync(
            new NotificationMessage("email", "user@example.com", SecretSubject, SecretBody),
            CancellationToken.None);

        Assert.Equal("email-1", result.DeliveryId);
        Assert.Equal(NotificationDeliveryStatus.Accepted, result.Status);
        Assert.Equal(SecretSubject, transport.LastSubject);
        Assert.Equal(SecretBody, transport.LastBody);
    }

    [Fact]
    public async Task Sms_provider_delegates_and_never_logs_content()
    {
        var transport = new RecordingSmsTransport();
        var provider = new SmsNotificationProvider(transport);

        var result = await provider.SendAsync(
            new NotificationMessage("sms", "+15551234567", null, SecretBody),
            CancellationToken.None);

        Assert.Equal("sms-1", result.DeliveryId);
        Assert.Equal(NotificationDeliveryStatus.Accepted, result.Status);
        Assert.Equal(SecretBody, transport.LastBody);
    }

    [Fact]
    public async Task Email_transport_never_emits_subject_or_body_to_logs()
    {
        var logger = new ListLogger<MailKitEmailTransport>();
        var transport = new MailKitEmailTransport(Options.Create(new SmtpNotificationOptions()), logger);

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => transport.SendAsync("user@example.com", SecretSubject, SecretBody, CancellationToken.None));

        Assert.NotNull(ex);
        Assert.DoesNotContain(logger.Messages,
            entry => entry.Contains(SecretSubject, StringComparison.Ordinal) ||
                     entry.Contains(SecretBody, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sms_transport_logs_recipient_but_not_body_url_or_api_key()
    {
        var logger = new ListLogger<HttpSmsTransport>();
        var handler = new SmsStubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"sid":"SM123"}""")
        });
        var transport = new HttpSmsTransport(
            new HttpClient(handler),
            Options.Create(new SmsNotificationOptions
            {
                ApiBaseUrl = "https://sms.example.test/v1/messages",
                ApiKey = "secret-gateway-key"
            }),
            Options.Create(new OutboundWebhookOptions()),
            logger);

        var deliveryId = await transport.SendAsync("+15551234567", SecretBody);

        Assert.Equal("SM123", deliveryId);
        Assert.NotEmpty(logger.Messages);
        Assert.Contains(logger.Messages, entry => entry.Contains("+15551234567", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages,
            entry => entry.Contains(SecretBody, StringComparison.Ordinal) ||
                     entry.Contains("secret-gateway-key", StringComparison.Ordinal) ||
                     entry.Contains("sms.example.test", StringComparison.Ordinal));
    }
}