using Microsoft.EntityFrameworkCore;
using SignalForge.Application.Services;
using SignalForge.Domain.Models;
using SignalForge.Infrastructure.Data;

namespace SignalForge.UnitTests.Services;

public class ApiKeyValidationServiceTests
{
    private const string ValidKey = "testkey12345abcdef";
    private const string SamePrefixWrongKey = "testkey1ZZZZZZZZ";
    private const string WrongPrefixKey = "zzzz9999abcdefgh";

    private static SignalForgeDbContext CreateDb()
        => new(new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<(ApiKeyValidationService Service, Guid TenantId)> CreateAsync(
        Action<ApiKey>? mutate = null)
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(Tenant.CreateWithId(tenantId, "key-test"));

        var apiKey = ApiKey.Create(tenantId, "webhook", ValidKey);
        mutate?.Invoke(apiKey);
        db.ApiKeys.Add(apiKey);
        await db.SaveChangesAsync();

        return (new ApiKeyValidationService(db), tenantId);
    }

    [Fact]
    public async Task Valid_key_resolves_the_correct_tenant()
    {
        var (service, tenantId) = await CreateAsync();

        var result = await service.ValidateApiKeyAsync(ValidKey);

        Assert.True(result.IsValid);
        Assert.Equal(tenantId, result.TenantId!.Value);
        Assert.Equal("webhook", result.ApiKeyName);
        Assert.False(result.ApiKeyId?.IsEmpty ?? true);
    }

    [Fact]
    public async Task Same_prefix_with_wrong_hash_is_rejected()
    {
        var (service, _) = await CreateAsync();

        var result = await service.ValidateApiKeyAsync(SamePrefixWrongKey);

        Assert.False(result.IsValid);
        Assert.Equal("Invalid API key", result.ErrorMessage);
    }

    [Fact]
    public async Task Wrong_prefix_is_rejected()
    {
        var (service, _) = await CreateAsync();

        var result = await service.ValidateApiKeyAsync(WrongPrefixKey);

        Assert.False(result.IsValid);
        Assert.Equal("Invalid API key", result.ErrorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_key_is_rejected(string? apiKey)
    {
        var (service, _) = await CreateAsync();

        var result = await service.ValidateApiKeyAsync(apiKey!);

        Assert.False(result.IsValid);
        Assert.Equal("API key is required", result.ErrorMessage);
    }

    [Fact]
    public async Task Deactivated_key_is_rejected()
    {
        var (service, _) = await CreateAsync(apiKey => apiKey.Deactivate());

        var result = await service.ValidateApiKeyAsync(ValidKey);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Valid_key_resolves_the_webhook_signing_secret_for_the_tenant()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(Tenant.CreateWithId(tenantId, "key-signing-test"));
        db.TenantWebhookSigningSettings.Add(TenantWebhookSigningSetting.Create(tenantId, "sf-secret-123"));
        db.ApiKeys.Add(ApiKey.Create(tenantId, "webhook", ValidKey));
        await db.SaveChangesAsync();

        var service = new ApiKeyValidationService(db);
        var result = await service.ValidateApiKeyAsync(ValidKey);

        Assert.True(result.IsValid);
        Assert.Equal("sf-secret-123", result.TenantWebhookSigningSecret);
    }

    [Fact]
    public async Task Valid_key_without_a_signing_setting_exposes_no_secret()
    {
        var (service, _) = await CreateAsync();

        var result = await service.ValidateApiKeyAsync(ValidKey);

        Assert.True(result.IsValid);
        Assert.Null(result.TenantWebhookSigningSecret);
    }

    [Fact]
    public async Task Legacy_unsalted_hash_is_upgraded_in_place_after_successful_validation()
    {
        var db = CreateDb();
        var tenantId = Guid.NewGuid();
        db.Tenants.Add(Tenant.CreateWithId(tenantId, "key-test"));
        var apiKey = ApiKey.Create(tenantId, "webhook", ValidKey);
        var legacyHash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ValidKey)));
        typeof(ApiKey).GetProperty(nameof(ApiKey.KeyHash))!.GetSetMethod(nonPublic: true)!
            .Invoke(apiKey, new object[] { legacyHash });
        db.ApiKeys.Add(apiKey);
        await db.SaveChangesAsync();

        var service = new ApiKeyValidationService(db);
        var result = await service.ValidateApiKeyAsync(ValidKey);

        Assert.True(result.IsValid);
        Assert.False(apiKey.RequiresKeyHashMigration);
        Assert.StartsWith("$pbkdf2-sha256$", apiKey.KeyHash);

        // Re-query the tracked entity to confirm the upgraded hash was persisted.
        var reloaded = await db.ApiKeys.SingleAsync();
        Assert.StartsWith("$pbkdf2-sha256$", reloaded.KeyHash);
        Assert.True(reloaded.VerifyKey(ValidKey));
    }
}