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
        // Salted-token auth: the raw password must never appear in the URL.
        Assert.DoesNotContain("secret", url);
        Assert.Contains("&t=", url);
        Assert.Contains("&s=", url);
        Assert.Contains("f=json", url);
        Assert.Contains("x=1", url);
    }

    [Fact]
    public void BuildSubsonicUrl_TokenIsMd5OfPasswordPlusSalt()
    {
        var connection = new SourceConnection
        {
            BaseUriOrPath = "https://navidrome.example.com",
            Username = "demo",
            TokenOrPassword = "secret",
            Type = SourceType.Navidrome
        };

        var url = NavidromeMediaSourceConnector.BuildSubsonicUrl(connection, "ping.view")!;
        var query = url.Split('?')[1].Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1]);

        var expected = Convert.ToHexString(
                System.Security.Cryptography.MD5.HashData(
                    System.Text.Encoding.UTF8.GetBytes("secret" + query["s"])))
            .ToLowerInvariant();
        Assert.Equal(expected, query["t"]);
    }
}
