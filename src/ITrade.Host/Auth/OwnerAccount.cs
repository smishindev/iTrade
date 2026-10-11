using NodaTime;

namespace ITrade.Host.Auth;

/// <summary>
/// The platform's single user: password hash, session stamp and the failed-login counter.
/// A new stamp (set with every new password) ends all existing sessions.
/// </summary>
internal sealed record OwnerAccount(string PasswordHash, Guid Stamp, int FailedAttempts, Instant? LockedUntil)
{
    public const int MaxFailedAttempts = 5;
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 256;

    public static readonly Duration LockoutDuration = Duration.FromMinutes(15);

    public static OwnerAccount New(string passwordHash) => new(passwordHash, Guid.NewGuid(), 0, null);

    /// <summary>Why the text cannot be a password, or null when it can.</summary>
    public static string? PasswordProblem(string? password) =>
        password is { Length: >= MinPasswordLength and <= MaxPasswordLength }
            ? null
            : $"The password must be {MinPasswordLength} to {MaxPasswordLength} characters long.";

    public bool IsLocked(Instant now) => LockedUntil is { } until && now < until;

    /// <summary>The fifth wrong password in a row locks the account; the count then starts again.</summary>
    public OwnerAccount AfterFailure(Instant now) =>
        FailedAttempts + 1 >= MaxFailedAttempts
            ? this with { FailedAttempts = 0, LockedUntil = now + LockoutDuration }
            : this with { FailedAttempts = FailedAttempts + 1, LockedUntil = null };

    public OwnerAccount AfterSuccess() => this with { FailedAttempts = 0, LockedUntil = null };
}

internal enum LoginOutcome
{
    Success,
    Failed,
    Locked,
}

internal readonly record struct LoginResult(LoginOutcome Outcome, Guid Stamp = default, Instant? LockedUntil = null);
