using Microsoft.Extensions.Logging.Abstractions;
using SignalForge.Application.Notifications;

namespace SignalForge.UnitTests;

public class WebhookNotificationProviderTests
{
    private static readonly NotificationMessage Message = new(
        "webhook", "https://echo.example.com/hook", "Subject", "Body");

    private static WebhookNotificationProvider Provider(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<WebhookNotificationProvider>.Instance);

    [Fact]
    public async Task Success_returns_provider_delivery_id_from_response_header()
    {
        HttpRequestMessage? captured = null;
        var provider = Provider(new FixedHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Headers = { { "X-Delivery-Id", "wh-srv-42" } }
            };
        }));

        var result = await provider.SendAsync(Message, CancellationToken.None);

        Assert.Equal("wh-srv-42", result.DeliveryId);
        Assert.Equal(NotificationDeliveryStatus.Accepted, result.Status);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal(Message.Recipient, captured!.RequestUri?.ToString());
        Assert.Equal("application/json", captured!.Content?.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Success_generates_delivery_id_when_header_is_absent()
    {
        var provider = Provider(new FixedHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)));

        var result = await provider.SendAsync(Message, CancellationToken.None);

        Assert.StartsWith("webhook-", result.DeliveryId);
    }

    [Fact]
    public async Task Non_2xx_response_throws_delivery_exception_with_status()
    {
        var provider = Provider(new FixedHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("nope")
        }));

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => provider.SendAsync(Message, CancellationToken.None));

        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task Invalid_recipient_url_throws_before_any_http_call()
    {
        var provider = Provider(new FixedHandler(_ => throw new InvalidOperationException("must not be reached")));

        await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => provider.SendAsync(Message with { Recipient = "not a url" }, CancellationToken.None));
    }

    [Theory]
    [InlineData("http://example.com/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("https://10.0.0.2/hook")]
    [InlineData("https://[::1]/hook")]
    public async Task Ssrf_target_recipient_throws_before_any_http_call(string recipient)
    {
        var provider = Provider(new FixedHandler(_ => throw new InvalidOperationException("must not be reached")));

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(
            () => provider.SendAsync(Message with { Recipient = recipient }, CancellationToken.None));

        Assert.DoesNotContain("must not be reached", ex.Message);
    }

    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FixedHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}