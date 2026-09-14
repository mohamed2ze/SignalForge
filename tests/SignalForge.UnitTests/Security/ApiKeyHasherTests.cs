using System.Security.Cryptography;
using System.Text;
using SignalForge.Domain.Security;

namespace SignalForge.UnitTests.Security;

public class ApiKeyHasherTests
{
    private const string PlainText = "sk-test-1234567890-abcdef-local";

    [Fact]
    public void Hash_returns_versioned_salted_format()
    {
        var hash = ApiKeyHasher.Hash(PlainText);

        Assert.StartsWith("$pbkdf2-sha256$", hash);
        Assert.DoesNotContain(PlainText, hash);
        Assert.Equal(5, hash.Split('$').Length);
    }

    [Fact]
    public void Hash_produces_distinct_values_for_same_key()
    {
        var first = ApiKeyHasher.Hash(PlainText);
        var second = ApiKeyHasher.Hash(PlainText);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Verify_roundtrips_versioned_hash()
    {
        var hash = ApiKeyHasher.Hash(PlainText);

        Assert.True(ApiKeyHasher.Verify(PlainText, hash));
        Assert.False(ApiKeyHasher.Verify("sk-test-9999999999-wrong", hash));
    }

    [Fact]
    public void Verify_accepts_legacy_unsalted_sha256_hash()
    {
        var legacy = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(PlainText)));

        Assert.True(ApiKeyHasher.Verify(PlainText, legacy));
        Assert.False(ApiKeyHasher.Verify("wrong-key", legacy));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("$pbkdf2-sha256$garbage")]
    public void Verify_rejects_malformed_storage(string storedHash)
    {
        Assert.False(ApiKeyHasher.Verify(PlainText, storedHash));
    }

    [Fact]
    public void IsVersionedHash_distinguishes_formats()
    {
        Assert.True(ApiKeyHasher.IsVersionedHash(ApiKeyHasher.Hash(PlainText)));
        Assert.False(ApiKeyHasher.IsVersionedHash(
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(PlainText)))));
    }
}