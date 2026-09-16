using Microsoft.EntityFrameworkCore;
using SignalForge.Infrastructure.Data;
using Testcontainers.MsSql;

namespace SignalForge.IntegrationTests;

/// <summary>
/// Shared xunit collection for all integration tests that need a real SQL Server.
/// Runs one ephemeral SQL Server container (Testcontainers) per test run; the container
/// replaces the previously required "manually running" SQL Server on localhost:1433.
/// All classes in this collection execute sequentially, which also removes the intermittent
/// SQL contention/duplicate-row flakiness seen when tests ran against the live dev DB.
/// </summary>
[CollectionDefinition(Name)]
public sealed class MsSqlCollection : ICollectionFixture<MsSqlContainerFixture>
{
    public const string Name = "MsSql";
}

/// <summary>
/// Starts the SQL Server container, applies the Infrastructure migrations once, then exposes
/// the mapped connection string to all tests in the collection.
/// </summary>
public sealed class MsSqlContainerFixture : IAsyncLifetime
{
    public const string Password = "YourStrong@Passw0rd";
    public const int DefaultTenantKeyPrefixLength = 8;

    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .WithPassword(Password)
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        var options = new DbContextOptionsBuilder<SignalForgeDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        using var dbContext = new SignalForgeDbContext(options);
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}