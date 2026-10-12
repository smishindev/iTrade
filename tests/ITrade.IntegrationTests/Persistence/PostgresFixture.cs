using ITrade.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ITrade.IntegrationTests.Persistence;

/// <summary>A throw-away PostgreSQL 18 (same image as deploy/docker-compose.yml) with all migrations applied.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;

    public PostgresFixture()
    {
        _container = new PostgreSqlBuilder("postgres:18")
            .WithDatabase("itrade")
            .WithUsername("itrade")
            .WithPassword(Secret)
            // Many tests take a database of their own, and each database keeps its pooled connections open for
            // a while after the test: more than the server's default of 100 once there were about a hundred tests.
            .WithCommand("-c", "max_connections=500")
            .Build();
    }

    /// <summary>The server's secret, new on every run (tests check it never reaches a log).</summary>
    public string Secret { get; } = Guid.NewGuid().ToString("N");

    public string ConnectionString => _container.GetConnectionString();

    public ITradeDbContext NewContext() => DbSetup.Create(ConnectionString);

    /// <summary>Connection string of a new, empty database (no migrations) in the same server.</summary>
    public async Task<string> NewEmptyDatabaseAsync()
    {
        var name = $"empty_{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
        await create.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    /// <summary>Runs a program inside the database container (pg_dump, pg_restore, createdb) and requires success.</summary>
    public async Task<string> RunInContainerAsync(params string[] command)
    {
        var result = await _container.ExecAsync(command);
        Assert.True(
            result.ExitCode == 0,
            $"{string.Join(' ', command)} exited with {result.ExitCode}: {result.Stderr}");
        return result.Stdout;
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>One database server for all integration tests (they run one after another).</summary>
[CollectionDefinition(Name)]
public sealed class PostgresTests : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
