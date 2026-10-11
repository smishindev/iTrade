using ITrade.Host.Logging;
using Microsoft.Extensions.Configuration;

namespace ITrade.UnitTests.Host;

public sealed class SecretMaskerTests
{
    private static SecretMasker MaskerFor(string connectionString) =>
        SecretMasker.For(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ITrade"] = connectionString })
            .Build());

    [Fact]
    public void KnownPasswordIsMaskedWhereverItAppears()
    {
        var masker = MaskerFor("Host=127.0.0.1;Port=55432;Username=itrade;Password=s3cr+t/Value==");

        var masked = masker.Apply("auth failed for itrade (tried s3cr+t/Value==), s3cr+t/Value== again");

        Assert.Equal("auth failed for itrade (tried ***), *** again", masked);
    }

    [Fact]
    public void PasswordWithQuotesIsMaskedInItsJsonForm()
    {
        var masker = new SecretMasker(["pa\"ss\\word"]);

        Assert.Equal("{\"m\":\"using ***\"}", masker.Apply("{\"m\":\"using pa\\\"ss\\\\word\"}"));
    }

    [Theory]
    [InlineData("Host=db;Password=hunter2;Port=1", "Host=db;Password=***;Port=1")]
    [InlineData("pwd = hunter2", "pwd = ***")]
    [InlineData("{\"token\":\"abc.def\",\"n\":1}", "{\"token\":\"***\",\"n\":1}")]
    [InlineData("{\\\"Api_Key\\\":\\\"abc\\\"}", "{\\\"Api_Key\\\":\\\"***\\\"}")]
    [InlineData("client secret: abc", "client secret: ***")]
    public void KeyValuePairsThatLookLikeSecretsAreMasked(string text, string expected)
    {
        Assert.Equal(expected, new SecretMasker([]).Apply(text));
    }

    [Theory]
    [InlineData("order 42 filled at 101.25 USD")]
    [InlineData("the password policy changed")]
    [InlineData("tokens are not used here")]
    public void OrdinaryTextIsLeftAlone(string text)
    {
        Assert.Equal(text, new SecretMasker(["s3cret-value"]).Apply(text));
    }

    [Fact]
    public void ConnectionStringThatCannotBeParsedIsMaskedWhole()
    {
        var masker = MaskerFor("not a connection string");

        Assert.Equal("opening ***", masker.Apply("opening not a connection string"));
    }
}
