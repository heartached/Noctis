using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class NavidromeConnectorTests
{
    [Fact]
    public void BuildSubsonicUrl_IncludesRequiredQueryParts()
    {
        var connection = new SourceConnection
        {
            BaseUriOrPath = "https://navidrome.example.com",
            Username = "demo",
            TokenOrPassword = "secret",
            Type = SourceType.Navidrome
        };

        var url = NavidromeMediaSourceConnector.BuildSubsonicUrl(connection, "ping.view", ("x", "1"));

        Assert.NotNull(url);
        Assert.Contains("/rest/ping.view?", url);
        Assert.Contains("u=demo", url);
        Assert.Contains("p=secret", url);
        Assert.Contains("f=json", url);
        Assert.Contains("x=1", url);
    }
}
