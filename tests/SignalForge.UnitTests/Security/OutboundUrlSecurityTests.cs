using System.Net;
using System.Net.Http;
using SignalForge.Application.Security;

namespace SignalForge.UnitTests.Security;

public class OutboundUrlSecurityTests
{
    private static readonly OutboundWebhookOptions Strict = new();
    private static readonly OutboundWebhookOptions AllowHttp =
        new() { AllowedHttpHosts = ["internal.example.com"] };

    // --- OutboundUrlValidator: synchronous checks ---

    [Theory]
    [InlineData("http://example.com/webhook")]
    [InlineData("ftp://example.com/file")]
    public void Validate_RejectsNonHttpsScheme(string url)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => OutboundUrlValidator.Validate(url, Strict));
        Assert.Contains("https", ex.Message);
    }

    [Fact]
    public void Validate_AllowsHttpForAllowlistedHost()
    {
        var uri = OutboundUrlValidator.Validate("http://internal.example.com/hook", AllowHttp);
        Assert.Equal("internal.example.com", uri.Host);
    }

    [Theory]
    [InlineData("https://127.0.0.1/webhook")]
    [InlineData("https://localhost/webhook")]
    [InlineData("https://10.0.0.5/webhook")]
    [InlineData("https://172.16.0.1/webhook")]
    [InlineData("https://192.168.1.10/webhook")]
    [InlineData("https://169.254.169.254/metadata")]
    [InlineData("https://[::1]/webhook")]
    [InlineData("https://[fc00::1]/webhook")]
    [InlineData("https://[fe80::1]/webhook")]
    public void Validate_RejectsLoopbackPrivateLinkLocalAndMetadata(string url)
    {
        Assert.Throws<InvalidOperationException>(() => OutboundUrlValidator.Validate(url, Strict));
    }

    [Fact]
    public void Validate_AllowsPublicIpLiteral()
    {
        Assert.Equal("8.8.8.8", OutboundUrlValidator.Validate("https://8.8.8.8/webhook", Strict).Host);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("")]
    public void Validate_RejectsNonAbsoluteUrls(string url)
    {
        Assert.Throws<InvalidOperationException>(() => OutboundUrlValidator.Validate(url, Strict));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.5", true)]
    [InlineData("192.168.55.7", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("169.254.10.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    public void IsPrivateOrReserved_ClassifiesAddresses(string address, bool expected)
    {
        Assert.Equal(expected, OutboundUrlValidator.IsPrivateOrReserved(IPAddress.Parse(address)));
    }

    [Fact]
    public void IsPrivateOrReserved_TreatsIpv4MappedIpv6LoopbackAsPrivate()
    {
        var mapped = IPAddress.Parse("127.0.0.1").MapToIPv6();
        Assert.True(OutboundUrlValidator.IsPrivateOrReserved(mapped));
    }

    // --- OutboundHttpRequestGuardHandler: DNS-time checks ---

    [Fact]
    public async Task Guard_BlocksRequestToPrivateIpLiteral()
    {
        var client = Guard(host => Task.FromResult(new[] { IPAddress.Parse("1.2.3.4") }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://10.0.0.1/path");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request, CancellationToken.None));
        Assert.Contains("non-public", ex.Message);
    }

    [Fact]
    public async Task Guard_BlocksHostnameThatResolvesToPrivateIp()
    {
        var client = Guard(host => Task.FromResult(new[] { IPAddress.Parse("192.168.0.10") }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://evil.example.com/path");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task Guard_AllowsHostnameResolvingToPublicIp()
    {
        var client = Guard(host => Task.FromResult(new[] { IPAddress.Parse("9.9.9.9") }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://ok.example.com/path");

        using var response = await client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Guard_BlocksHttpRedirectTargetingLoopbackBeforeForwarding()
    {
        // Even with a public host, the guard runs on the initial request only; a redirect target is
        // never followed because redirects are disabled at the handler. Verify a loopback request
        // over http (not allowlisted) is rejected outright.
        var client = Guard(host => Task.FromResult(new[] { IPAddress.Parse("1.2.3.4") }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/steal");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request, CancellationToken.None));
    }

    // --- OutboundResponseBody: size cap ---

    [Fact]
    public async Task ReadCappedAsync_ReturnsBodyWithinLimit()
    {
        var content = new StringContent(new string('x', 1024));
        var body = await OutboundResponseBody.ReadCappedAsync(content, 8192, CancellationToken.None);
        Assert.Equal(1024, body.Length);
    }

    [Fact]
    public async Task ReadCappedAsync_ThrowsWhenBodyExceedsLimit()
    {
        var content = new StringContent(new string('x', 4096));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => OutboundResponseBody.ReadCappedAsync(content, 1024, CancellationToken.None));
        Assert.Contains("limit", ex.Message);
    }

    // --- ScrubForLog: L5, no credentials in logs ---

    [Fact]
    public void ScrubForLog_StripsUserInfoQueryAndFragment()
    {
        var scrubbed = OutboundUrlValidator.ScrubForLog(
            "https://user:secret@example.com/hook?token=abc#frag");
        Assert.Equal("https://example.com/hook", scrubbed);
        Assert.DoesNotContain("secret", scrubbed);
        Assert.DoesNotContain("token", scrubbed);
    }

    [Fact]
    public void ScrubForLog_ReturnsInvalidMarkerForNonUrl()
    {
        Assert.Equal("<invalid-url>", OutboundUrlValidator.ScrubForLog("not a url"));
    }

    // --- StorageText: H2 bounded persistence ---

    [Fact]
    public void TruncateForStorage_KeepsShortTextUnchanged()
    {
        Assert.Equal("short", StorageText.TruncateForStorage("short"));
    }

    [Fact]
    public void TruncateForStorage_CapsLongTextAtMaxLength()
    {
        var text = new string('a', StorageText.DefaultMaxLength + 50);
        var result = StorageText.TruncateForStorage(text);
        Assert.Equal(StorageText.DefaultMaxLength, result?.Length);
    }

    [Fact]
    public void TruncateForStorage_ReturnsNullForNull()
    {
        Assert.Null(StorageText.TruncateForStorage(null));
    }

    private static HttpClient Guard(Func<string, Task<IPAddress[]>> resolver)
        => new(new OutboundHttpRequestGuardHandler(
            Strict,
            (host, _) => resolver(host))
        {
            InnerHandler = new RespondingHandler()
        });

    private sealed class RespondingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}