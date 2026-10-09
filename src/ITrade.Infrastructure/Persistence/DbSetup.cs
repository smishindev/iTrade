using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ITrade.Infrastructure.Persistence;

public static class DbSetup
{
    /// <summary>The one place the provider and naming are configured (Host, tests, design time).</summary>
    public static DbContextOptionsBuilder<ITradeDbContext> Configure(
        DbContextOptionsBuilder<ITradeDbContext> builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .UseNpgsql(connectionString, npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention();
    }

    public static ITradeDbContext Create(string connectionString) =>
        new(Configure(new DbContextOptionsBuilder<ITradeDbContext>(), connectionString).Options);
}

/// <summary>
/// Used by `dotnet ef migrations add` only. It never connects, so the connection string has no password;
/// migrations are applied by the Host and the integration tests.
/// </summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<ITradeDbContext>
{
    public ITradeDbContext CreateDbContext(string[] args) =>
        DbSetup.Create("Host=localhost;Port=55432;Database=itrade;Username=itrade");
}
