using ITrade.Application.Jobs;
using ITrade.Domain;
using ITrade.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace ITrade.IntegrationTests.Persistence;

/// <summary>
/// P2.3.05: the backup procedure of deploy/backup.ps1 and deploy/restore.ps1 — the same programs with the same
/// flags — gives back the data, the migration history and the append-only guard of the audit journal.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class BackupRestoreTests(PostgresFixture pg)
{
    private const string User = "itrade";

    private static readonly Instant At = Instant.FromUtc(2026, 10, 12, 7, 0);

    [Fact]
    public async Task DumpRestoredIntoANewDatabaseHasTheDataTheHistoryAndTheJournalGuard()
    {
        var source = await pg.NewEmptyDatabaseAsync();
        var sourceName = new NpgsqlConnectionStringBuilder(source).Database!;
        var eventId = Guid.CreateVersion7();
        var session = new TradeDate(new LocalDate(2026, 10, 9));
        IReadOnlyList<string> migrations;
        await using (var db = DbSetup.Create(source))
        {
            await db.Database.MigrateAsync();
            db.Settings.Add(new Setting { Key = "probe", Value = "{\"amount\": 1234.5678}", UpdatedAt = At });
            db.AuditEvents.Add(new AuditEvent
            {
                Id = eventId,
                OccurredAt = At,
                Actor = "test",
                Kind = "probe",
                Payload = "{\"text\": \"שלום\"}",
            });
            db.JobRuns.Add(new JobRun
            {
                Name = "job",
                TradeDate = session,
                Status = JobRunStatus.Succeeded,
                Attempts = 2,
                StartedAt = At,
                FinishedAt = At,
            });
            await db.SaveChangesAsync();
            migrations = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        }

        // The flags of deploy/backup.ps1 and deploy/restore.ps1: keep them in step.
        var dump = $"/tmp/{sourceName}.dump";
        var restoredName = $"restored_{Guid.NewGuid():N}";
        await pg.RunInContainerAsync("pg_dump", "-U", User, "-d", sourceName, "--format=custom", $"--file={dump}");
        await pg.RunInContainerAsync("createdb", "-U", User, restoredName);
        await pg.RunInContainerAsync("pg_restore", "-U", User, "-d", restoredName, "--exit-on-error", dump);

        var restored = new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Database = restoredName }.ConnectionString;
        await using var copy = DbSetup.Create(restored);
        Assert.Equal(migrations, await copy.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await copy.Database.GetPendingMigrationsAsync());

        var setting = await copy.Settings.SingleAsync(s => s.Key == "probe");
        Assert.Equal(At, setting.UpdatedAt);
        Assert.Contains("1234.5678", setting.Value, StringComparison.Ordinal);
        var audit = await copy.AuditEvents.SingleAsync(a => a.Id == eventId);
        Assert.Equal((At, "test", "probe"), (audit.OccurredAt, audit.Actor, audit.Kind));
        Assert.Contains("שלום", audit.Payload, StringComparison.Ordinal);
        var run = await copy.JobRuns.SingleAsync();
        Assert.Equal(("job", session, JobRunStatus.Succeeded, 2), (run.Name, run.TradeDate, run.Status, run.Attempts));

        // The journal of the restored database is still append-only.
        await using var connection = new NpgsqlConnection(restored);
        await connection.OpenAsync();
        await using var update = new NpgsqlCommand($"UPDATE audit_events SET kind = 'changed' WHERE id = '{eventId}'", connection);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task RestoreIntoADatabaseThatAlreadyHasTheTablesFailsInsteadOfMixingData()
    {
        var source = await pg.NewEmptyDatabaseAsync();
        var sourceName = new NpgsqlConnectionStringBuilder(source).Database!;
        await using (var db = DbSetup.Create(source))
        {
            await db.Database.MigrateAsync();
        }

        var dump = $"/tmp/{sourceName}.dump";
        await pg.RunInContainerAsync("pg_dump", "-U", User, "-d", sourceName, "--format=custom", $"--file={dump}");

        // Restoring over the database the dump came from: --exit-on-error stops at the first existing table.
        var error = await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() =>
            pg.RunInContainerAsync("pg_restore", "-U", User, "-d", sourceName, "--exit-on-error", dump));
        Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
    }
}
