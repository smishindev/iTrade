using ITrade.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace ITrade.IntegrationTests.Persistence;

/// <summary>P2.3.02–P2.3.04: migrations on a clean PostgreSQL 18, append-only audit journal, model conventions.</summary>
public sealed class DatabaseTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly Instant At = Instant.FromUtc(2026, 10, 9, 5, 0);

    [Fact]
    public async Task MigrationsCreateTheTables()
    {
        await using var db = pg.NewContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync();
        Assert.Contains("settings", tables);
        Assert.Contains("audit_events", tables);
        Assert.Contains("jobs", tables);
    }

    [Fact]
    public async Task AuditJournalAcceptsInsertsButRejectsUpdateDeleteTruncate()
    {
        var id = Guid.CreateVersion7();
        await using (var db = pg.NewContext())
        {
            db.AuditEvents.Add(new AuditEvent { Id = id, OccurredAt = At, Actor = "test", Kind = "probe", Payload = "{}" });
            await db.SaveChangesAsync();
        }

        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
                 {
                     $"UPDATE audit_events SET kind = 'changed' WHERE id = '{id}'",
                     $"DELETE FROM audit_events WHERE id = '{id}'",
                     "TRUNCATE audit_events",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            Assert.Contains("append-only", error.MessageText, StringComparison.Ordinal);
        }

        await using var check = pg.NewContext();
        var kept = await check.AuditEvents.SingleAsync(a => a.Id == id);
        Assert.Equal("probe", kept.Kind);
    }

    [Fact]
    public async Task InstantsRoundTripAsUtc()
    {
        await using (var db = pg.NewContext())
        {
            db.Settings.Add(new Setting { Key = "probe", Value = "{\"a\":1}", UpdatedAt = At });
            await db.SaveChangesAsync();
        }

        await using var read = pg.NewContext();
        Assert.Equal(At, (await read.Settings.SingleAsync(s => s.Key == "probe")).UpdatedAt);
    }

    [Fact]
    public void ModelHasNoFloatingPointMoneyAndNoDateTime()
    {
        using var db = pg.NewContext();
        var properties = db.Model.GetEntityTypes().SelectMany(e => e.GetProperties()).ToList();
        var forbidden = new[] { typeof(double), typeof(float), typeof(DateTime), typeof(DateTimeOffset) };
        var bad = properties
            .Where(p => forbidden.Contains(Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType))
            .Select(p => $"{p.DeclaringType.DisplayName()}.{p.Name}: {p.ClrType.Name}")
            .ToList();
        Assert.True(bad.Count == 0, string.Join(", ", bad));
        var decimals = properties.Where(p => p.GetProviderClrType() == typeof(decimal) || p.ClrType == typeof(decimal));
        Assert.All(decimals, p => Assert.NotNull(p.GetPrecision()));
    }
}
