using SignalForge.Application.Security;

namespace SignalForge.UnitTests.Security;

public class EventSignatureVerifierTests
{
    private const string Secret = "test-signing-secret-123";

    [Fact]
    public void Verify_WithValidSignatureSignature_ReturnsTrue()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = """{"externalEventId":"e-1","eventType":"order.created","payload":"{"orderId":42}"}""";

        var signature = EventSignatureVerifier.ComputeSignature(Secret, timestamp, body);

        Assert.True(EventSignatureVerifier.Verify(Secret, timestamp, body, signature));
    }

    [Fact]
    public void Verify_WithTamperedBody_ReturnsFalse()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var original = """{"externalEventId":"e-1","eventType":"order.created","payload":"{"orderId":42}"}""";
        var tampered = original.Replace("42", "43");
        var signature = EventSignatureVerifier.ComputeSignature(Secret, timestamp, original);

        Assert.False(EventSignatureVerifier.Verify(Secret, timestamp, tampered, signature));
    }

    [Fact]
    public void Verify_WithWrongSecret_ReturnsFalse()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = """{"externalEventId":"e-1","eventType":"order.created"}""";
        var signature = EventSignatureVerifier.ComputeSignature("other-secret", timestamp, body);

        Assert.False(EventSignatureVerifier.Verify(Secret, timestamp, body, signature));
    }

    [Theory]
    [InlineData(-600)]   // 10 minutes in the past
    [InlineData(600)]    // 10 minutes in the future
    public void Verify_OutsideFreshnessWindow_ReturnsFalse(long offsetSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.AddSeconds(offsetSeconds).ToUnixTimeSeconds();
        var body = """{"externalEventId":"e-1"}""";
        var signature = EventSignatureVerifier.ComputeSignature(Secret, timestamp, body);

        Assert.False(EventSignatureVerifier.Verify(Secret, timestamp, body, signature, now));
    }

    [Theory]
    [InlineData(-299)]
    [InlineData(299)]
    public void Verify_InsideFreshnessWindow_ReturnsTrue(long offsetSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.AddSeconds(offsetSeconds).ToUnixTimeSeconds();
        var body = """{"externalEventId":"e-1"}""";
        var signature = EventSignatureVerifier.ComputeSignature(Secret, timestamp, body);

        Assert.True(EventSignatureVerifier.Verify(Secret, timestamp, body, signature, now));
    }

    [Fact]
    public void Verify_WithWrongSchemePrefix_ReturnsFalse()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = """{"externalEventId":"e-1"}""";
        var signature = EventSignatureVerifier.ComputeSignature(Secret, timestamp, body).Replace("sha256=", "md5=");

        Assert.False(EventSignatureVerifier.Verify(Secret, timestamp, body, signature));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("not-a-signature")]
    public void Verify_WithMalformedSignatureHeader_ReturnsFalse(string malformed)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = """{"externalEventId":"e-1"}""";

        Assert.False(EventSignatureVerifier.Verify(Secret, timestamp, body, malformed));
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("1")]
    [InlineData("0")]
    public void TryParseUnixSeconds_WithValidValue_ReturnsTrue(string value)
    {
        Assert.True(EventSignatureVerifier.TryParseUnixSeconds(value, out var result));
        Assert.Equal(long.Parse(value), result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("-")]
    [InlineData("1.5")]
    [InlineData("2024-01-01T00:00:00Z")]
    public void TryParseUnixSeconds_WithInvalidValue_ReturnsFalse(string? value)
    {
        Assert.False(EventSignatureVerifier.TryParseUnixSeconds(value, out var result));
        Assert.Equal(0, result);
    }

    [Fact]
    public void ComputeSignature_IsDeterministicAndLowercaseHex()
    {
        var timestamp = 1_700_000_000L;
        var body = "raw-body";

        var signature = EventSignatureVerifier.ComputeSignature(Secret, timestamp, body);
        var signatureAgain = EventSignatureVerifier.ComputeSignature(Secret, timestamp, body);

        Assert.Equal(signature, signatureAgain);
        Assert.StartsWith("sha256=", signature);
        var hex = signature["sha256=".Length..];
        Assert.Equal(64, hex.Length);
        Assert.Equal(hex, hex.ToLowerInvariant());
        Assert.IsType<long>(timestamp);
    }
}