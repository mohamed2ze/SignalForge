using SignalForge.Application.Security;

namespace SignalForge.UnitTests.Security;

/// <summary>
/// Regression pins for L3: anything persisted from an exception dump (or webhook/event body)
/// must be bounded in size and free of credentials before it reaches a storage column.
/// </summary>
public class StorageTextTests
{
    // ---------- truncation ----------

    [Fact]
    public void TruncateForStorage_Null_Returns_Null()
    {
        Assert.Null(StorageText.TruncateForStorage(null));
    }

    [Fact]
    public void TruncateForStorage_Long_Value_Is_Capped()
    {
        var value = new string('a', 20_000);
        var result = StorageText.TruncateForStorage(value, maxLength: 1_024);

        Assert.Equal(1_024, result!.Length);
    }

    // ---------- scrub naive redaction ----------

    [Fact]
    public void ScrubForStorage_Redacts_Url_Credentials_In_Query()
    {
        var result = StorageText.ScrubForStorage(
            "Failed calling https://user:secret@hooks.example.com/boom?token=abc123&x=1#frag");

        Assert.DoesNotContain("user:secret", result);
        Assert.DoesNotContain("token=abc123", result);
        Assert.DoesNotContain("#frag", result);
        Assert.Contains("hooks.example.com", result);
    }

    [Fact]
    public void ScrubForStorage_Redacts_Password_Parameter_Values()
    {
        var result = StorageText.ScrubForStorage("connect failed password=hunter2&port=5432");
        Assert.DoesNotContain("hunter2", result);
        Assert.Contains("password=***", result);
        Assert.Contains("port=5432", result);
    }

    [Fact]
    public void ScrubForStorage_Redacts_Json_Quoted_Secrets()
    {
        var result = StorageText.ScrubForStorage(""""{"apiKey":"sk-live-abc","payload":{"token":"tkn-123"}}"""");
        Assert.DoesNotContain("sk-live-abc", result);
        Assert.DoesNotContain("tkn-123", result);
    }

    [Fact]
    public void ScrubForStorage_Redacts_Authorization_Header()
    {
        var result = StorageText.ScrubForStorage(
            "POST /hook HTTP/1.1\r\nAuthorization: Bearer eyJhbGc.abc.xyz\r\nContent-Length: 0");
        Assert.DoesNotContain("eyJhbGc.abc.xyz", result);
        Assert.Contains("Authorization: ***", result);
    }

    [Fact]
    public void ScrubForStorage_Redacts_Standalone_Jwt()
    {
        var header = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.sig123456";
        var result = StorageText.ScrubForStorage($"event body contained {header} after commit");

        Assert.DoesNotContain(header, result);
        Assert.Contains("<jwt-redacted>", result);
    }

    [Fact]
    public void ScrubForStorage_Truncates_After_Redaction()
    {
        var result = StorageText.ScrubForStorage(
            $"token=super-secret-{new string('x', 4_000)}",
            maxLength: 128);

        Assert.NotNull(result);
        Assert.True(result!.Length <= 128);
        Assert.DoesNotContain("super-secret", result);
    }

    [Fact]
    public void ScrubForStorage_Keeps_Plain_Text_Untouched()
    {
        const string plain = "workflow step 3 failed: timeout after 5s";
        Assert.Equal(
            plain,
            StorageText.ScrubForStorage(plain, maxLength: plain.Length));
    }

    [Fact]
    public void ScrubForStorage_Null_Returns_Null()
    {
        Assert.Null(StorageText.ScrubForStorage(null));
    }
}