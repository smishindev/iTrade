using System.Text.Json;
using ITrade.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;

namespace ITrade.Host.Auth;

/// <summary>
/// Stores the owner's account in the `settings` table (key <see cref="Key"/>) and checks passwords.
/// Every outcome is written to the audit journal; the password itself is never stored or logged.
/// </summary>
internal sealed class OwnerAccounts(ITradeDbContext db, IClock clock)
{
    public const string Key = "auth.owner";
    public const string Owner = "owner";

    private readonly PasswordHasher<string> _hasher = new();

    private static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    public async Task<OwnerAccount?> FindAsync(CancellationToken cancellationToken)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == Key, cancellationToken);
        return row is null ? null : Parse(row.Value);
    }

    /// <summary>Sets (or replaces) the password; existing sessions end and any lockout is cleared.</summary>
    public async Task SetPasswordAsync(string password, string actor, CancellationToken cancellationToken)
    {
        if (OwnerAccount.PasswordProblem(password) is { } problem)
        {
            throw new ArgumentException(problem, nameof(password));
        }

        var now = clock.GetCurrentInstant();
        var value = Format(OwnerAccount.New(_hasher.HashPassword(Owner, password)));
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == Key, cancellationToken);
        if (row is null)
        {
            db.Settings.Add(new Setting { Key = Key, Value = value, UpdatedAt = now });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = now;
        }

        Audit(now, actor, "auth.password_set");
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Signing out ends every session of the owner, not only this browser's: a cookie copied elsewhere dies
    /// with it, and so do open event connections.
    /// </summary>
    public async Task EndAllSessionsAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.Settings
            .FromSql($"SELECT * FROM settings WHERE key = {Key} FOR UPDATE")
            .ToListAsync(cancellationToken);
        if (rows.SingleOrDefault() is not { } row)
        {
            return;
        }

        var now = clock.GetCurrentInstant();
        row.Value = Format(Parse(row.Value) with { Stamp = Guid.NewGuid() });
        row.UpdatedAt = now;
        Audit(now, Owner, "auth.logout");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Checks a password. The account row is locked for the check, so parallel attempts cannot slip past
    /// the failed-attempt limit; a locked account is refused without looking at the password.
    /// </summary>
    public async Task<LoginResult> LoginAsync(string password, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.Settings
            .FromSql($"SELECT * FROM settings WHERE key = {Key} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        if (rows.SingleOrDefault() is not { } row)
        {
            return new LoginResult(LoginOutcome.Failed);
        }

        var account = Parse(row.Value);
        if (account.IsLocked(now))
        {
            return new LoginResult(LoginOutcome.Locked, LockedUntil: account.LockedUntil);
        }

        var verdict = OwnerAccount.PasswordProblem(password) is null
            ? _hasher.VerifyHashedPassword(Owner, account.PasswordHash, password)
            : PasswordVerificationResult.Failed;
        LoginResult result;
        if (verdict == PasswordVerificationResult.Failed)
        {
            account = account.AfterFailure(now);
            Audit(now, "anonymous", account.IsLocked(now) ? "auth.locked" : "auth.login_failed");
            result = new LoginResult(LoginOutcome.Failed);
        }
        else
        {
            account = account.AfterSuccess();
            if (verdict == PasswordVerificationResult.SuccessRehashNeeded)
            {
                account = account with { PasswordHash = _hasher.HashPassword(Owner, password) };
            }

            Audit(now, Owner, "auth.login");
            result = new LoginResult(LoginOutcome.Success, account.Stamp);
        }

        row.Value = Format(account);
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private void Audit(Instant now, string actor, string kind) =>
        db.AuditEvents.Add(new AuditEvent { OccurredAt = now, Actor = actor, Kind = kind, Payload = "{}" });

    private static string Format(OwnerAccount account) => JsonSerializer.Serialize(
        new Stored(
            account.PasswordHash,
            account.Stamp,
            account.FailedAttempts,
            account.LockedUntil is { } until ? InstantPattern.ExtendedIso.Format(until) : null),
        Json);

    private static OwnerAccount Parse(string json)
    {
        var stored = JsonSerializer.Deserialize<Stored>(json, Json)
            ?? throw new InvalidOperationException($"Setting '{Key}' is empty.");
        return new OwnerAccount(
            stored.PasswordHash,
            stored.Stamp,
            stored.FailedAttempts,
            stored.LockedUntil is null ? null : InstantPattern.ExtendedIso.Parse(stored.LockedUntil).Value);
    }

    private sealed record Stored(string PasswordHash, Guid Stamp, int FailedAttempts, string? LockedUntil);
}
