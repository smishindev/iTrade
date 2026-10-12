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
    [InlineData("access_token=abcd1234; next", "access_token=***; next")] // the word ends a longer name
    [InlineData("POSTGRES_PASSWORD=abc123", "POSTGRES_PASSWORD=***")]
    [InlineData("client_secret: xyz", "client_secret: ***")]
    [InlineData("{\"antiforgeryToken\":\"CfDJ8xyz\",\"n\":1}", "{\"antiforgeryToken\":\"***\",\"n\":1}")]
    [InlineData("{\"passwordHash\":\"AQAAAA\"}", "{\"passwordHash\":\"***\"}")] // …or starts one
    [InlineData("Host=db;Psw=hunter2", "Host=db;Psw=***")]
    public void KeyValuePairsThatLookLikeSecretsAreMasked(string text, string expected)
    {
        Assert.Equal(expected, new SecretMasker([]).Apply(text));
    }

    [Theory]
    [InlineData("order 42 filled at 101.25 USD")]
    [InlineData("the password policy changed")]
    [InlineData("tokens are not used here")]
    [InlineData("{\"antiforgeryHeader\":\"X-XSRF-TOKEN\",\"n\":1}")] // the word inside a value, not a name
    public void OrdinaryTextIsLeftAlone(string text)
    {
        Assert.Equal(text, new SecretMasker(["s3cret-value"]).Apply(text));
    }

    [Fact]
    public void EverySettingNamedLikeASecretIsKnownByItsValue()
    {
        var flex = Guid.NewGuid().ToString("N");
        var broker = Guid.NewGuid().ToString("N");
        var masker = SecretMasker.For(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Flex:Token"] = flex,
                ["Broker:ApiKey"] = broker,
                ["ITrade:DataDir"] = "D:/ITradeData",
                ["ConnectionStrings:ITrade"] = "Host=db;Psw=from-a-connection-string",
            })
            .Build());

        var masked = masker.Apply($"request {flex} with {broker} to D:/ITradeData as from-a-connection-string");

        Assert.Equal("request *** with *** to D:/ITradeData as ***", masked);
    }

    [Fact]
    public void ConnectionStringThatCannotBeParsedIsMaskedWhole()
    {
        var masker = MaskerFor("not a connection string");

        Assert.Equal("opening ***", masker.Apply("opening not a connection string"));
    }
}
