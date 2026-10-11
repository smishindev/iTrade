using ITrade.Host.Auth;
using NodaTime;

namespace ITrade.UnitTests.Host;

public sealed class OwnerAccountTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 12, 8, 0);

    [Fact]
    public void FifthFailureInARowLocksFor15MinutesAndRestartsTheCount()
    {
        var account = OwnerAccount.New("hash");
        for (var i = 1; i < OwnerAccount.MaxFailedAttempts; i++)
        {
            account = account.AfterFailure(Now);
            Assert.Equal(i, account.FailedAttempts);
            Assert.False(account.IsLocked(Now));
        }

        account = account.AfterFailure(Now);

        Assert.Equal(0, account.FailedAttempts);
        Assert.True(account.IsLocked(Now));
        Assert.True(account.IsLocked(Now + OwnerAccount.LockoutDuration - Duration.FromSeconds(1)));
        Assert.False(account.IsLocked(Now + OwnerAccount.LockoutDuration));
    }

    [Fact]
    public void SuccessClearsTheCountAndKeepsTheSession()
    {
        var account = OwnerAccount.New("hash").AfterFailure(Now).AfterFailure(Now);

        var after = account.AfterSuccess();

        Assert.Equal(0, after.FailedAttempts);
        Assert.Null(after.LockedUntil);
        Assert.Equal(account.Stamp, after.Stamp);
    }

    [Fact]
    public void EveryNewAccountHasItsOwnSessionStamp()
    {
        Assert.NotEqual(OwnerAccount.New("hash").Stamp, OwnerAccount.New("hash").Stamp);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("elevenchars", false)]
    [InlineData("twelve-chars", true)]
    public void PasswordLengthIsChecked(string? text, bool accepted)
    {
        Assert.Equal(accepted, OwnerAccount.PasswordProblem(text) is null);
        Assert.NotNull(OwnerAccount.PasswordProblem(new string('x', OwnerAccount.MaxPasswordLength + 1)));
    }
}
