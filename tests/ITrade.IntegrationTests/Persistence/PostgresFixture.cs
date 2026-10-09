using ITrade.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace ITrade.IntegrationTests.Persistence;

/// <summary>A throw-away PostgreSQL 18 (same image as deploy/docker-compose.yml) with all migrations applied.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("itrade")
        .WithUsername("itrade")
        .WithPassword("test-only")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public ITradeDbContext NewContext() => DbSetup.Create(ConnectionString);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
