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
}