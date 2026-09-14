using SignalForge.Domain.Models;

namespace SignalForge.UnitTests.Domain;

public class ApiKeyTests
{
    private const string PlainText = "sk-test-1234567890-abcdef-local";

    [Fact]
    public void Create_stores_hash_not_plaintext()
    {
        var key = ApiKey.Create(Guid.NewGuid(), "my-key", PlainText);

        Assert.NotEqual(PlainText, key.KeyHash);
        Assert.NotEmpty(key.KeyHash);
        Assert.Equal("sk-test-", key.KeyPrefix);
        Assert.True(key.IsActive);
        Assert.Null(key.RevokedAt);
        Assert.Null(key.ExpiresAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_name(string? name)
    {
        Assert.Throws<ArgumentException>(() => ApiKey.Create(Guid.NewGuid(), name!, PlainText));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_key(string? key)
    {
        Assert.Throws<ArgumentException>(() => ApiKey.Create(Guid.NewGuid(), "name", key!));
    }

    [Fact]
    public void VerifyKey_accepts_the_correct_key()
    {
        var key = ApiKey.Create(Guid.NewGuid(), "my-key", PlainText);

        Assert.True(key.VerifyKey(PlainText));
    }

    [Fact]
    public void VerifyKey_rejects_wrong_and_blank_keys()
    {
        var key = ApiKey.Create(Guid.NewGuid(), "my-key", PlainText);

        Assert.False(key.VerifyKey("sk-test-9999999999-wrong"));
        Assert.False(key.VerifyKey(""));
        Assert.False(key.VerifyKey("   "));
    }

    [Fact]
    public void VerifyKey_is_consistent_across_instances()
    {
        var a = ApiKey.Create(Guid.NewGuid(), "one", PlainText);
        var b = ApiKey.Create(Guid.NewGuid(), "two", PlainText);

        // Salting: same plaintext yields distinct stored hashes, but both verify.
        Assert.NotEqual(a.KeyHash, b.KeyHash);
        Assert.True(a.VerifyKey(PlainText));
        Assert.True(b.VerifyKey(PlainText));
    }

    [Fact]
    public void Create_uses_versioned_salted_hash()
    {
        var key = ApiKey.Create(Guid.NewGuid(), "my-key", PlainText);

        Assert.StartsWith("$pbkdf2-sha256$", key.KeyHash);
        Assert.False(key.RequiresKeyHashMigration);
    }

    [Fact]
    public void Legacy_sha256_hash_verifies_and_flags_migration()
    {
        var legacyHash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(PlainText)));
        var key = ApiKey.Create(Guid.NewGuid(), "my-key", "ignored");

        typeof(ApiKey).GetProperty(nameof(ApiKey.KeyHash))!.GetSetMethod(nonPublic: true)!
            .Invoke(key, new object[] { legacyHash });

        Assert.True(key.RequiresKeyHashMigration);
        Assert.True(key.VerifyKey(PlainText));
        Assert.False(key.VerifyKey("wrong-key"));
    }

    [Fact]
    public void RehashKey_upgrades_legacy_hash_and_keeps_verification()
    {
        var legacyHash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(PlainText)));
        var key = ApiKey.Create(Guid.NewGuid(), "my-key", "ignored");
        typeof(ApiKey).GetProperty(nameof(ApiKey.KeyHash))!.GetSetMethod(nonPublic: true)!
            .Invoke(key, new object[] { legacyHash });

        key.RehashKey(PlainText);

        Assert.False(key.RequiresKeyHashMigration);
        Assert.StartsWith("$pbkdf2-sha256$", key.KeyHash);
        Assert.True(key.VerifyKey(PlainText));
    }

    [Fact]
    public void UpdateInfo_trims_name()
    {
        var key = ApiKey.Create(Guid.NewGuid(), "old", PlainText);

        key.UpdateInfo("  new name  ");

        Assert.Equal("new name", key.Name);
    }

    [Fact]
    public void UpdateInfo_rejects_blank_name()
    {
        var key = ApiKey.Create(Guid.NewGuid(), "old", PlainText);

        Assert.Throws<ArgumentException>(() => key.UpdateInfo(" "));
    }

    [Fact]
    public void IsValid_reflects_active_lifecycle()
    {
        var key = ApiKey.Create(Guid.NewGuid(), "my-key", PlainText);

        Assert.True(key.IsValid());

        key.Deactivate();
        Assert.False(key.IsValid());

        key.Activate();
        Assert.True(key.IsValid());
    }

    [Fact]
    public void Revoke_permanently_invalidates()
    {
        var key = ApiKey.Create(Guid.NewGuid(), "my-key", PlainText);

        key.Revoke();

        Assert.False(key.IsActive);
        Assert.NotNull(key.RevokedAt);
        Assert.False(key.IsValid());
    }

    [Fact]
    public void MarkAsUsed_tracks_last_use()
    {
        var key = ApiKey.Create(Guid.NewGuid(), "my-key", PlainText);
        var before = DateTime.UtcNow;
        System.Threading.Thread.Sleep(5);

        key.MarkAsUsed();

        Assert.True(key.LastUsedAt >= before);
    }

    [Fact]
    public void IsExpired_is_false_when_no_expiry()
    {
        var key = ApiKey.Create(Guid.NewGuid(), "my-key", PlainText);

        Assert.False(key.IsExpired());
    }
}