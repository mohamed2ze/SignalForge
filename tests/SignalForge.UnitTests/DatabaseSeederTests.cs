using Microsoft.EntityFrameworkCore;
using SignalForge.Infrastructure.Data;

namespace SignalForge.UnitTests;

public class DatabaseSeederTests
{
    private static SignalForgeDbContext CreateDb()
        => new(new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private const string AllowedChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    [Fact]
    public async Task EnsureSeedDataAsync_generates_a_cryptographically_random_key_of_correct_shape()
    {
        var db = CreateDb();

        var seed = await DatabaseSeeder.EnsureSeedDataAsync(db);

        Assert.NotNull(seed.ApiKey);
        Assert.Equal(32, seed.ApiKey!.Length);
        Assert.All(seed.ApiKey, c => Assert.Contains(c, AllowedChars));

        // A fresh seed yields a different key (no deterministic reuse of the sample value).
        var seed2 = await DatabaseSeeder.EnsureSeedDataAsync(CreateDb());
        Assert.NotNull(seed2.ApiKey);
        Assert.NotEqual(seed.ApiKey, seed2.ApiKey);
    }

    [Fact]
    public async Task EnsureSeedDataAsync_is_idempotent()
    {
        var db = CreateDb();
        var first = await DatabaseSeeder.EnsureSeedDataAsync(db);
        var second = await DatabaseSeeder.EnsureSeedDataAsync(db);

        Assert.NotNull(first.ApiKey);
        Assert.Null(second.ApiKey);
        Assert.Equal(1, await db.ApiKeys.CountAsync());
        Assert.Equal(1, await db.Tenants.CountAsync());
    }
}