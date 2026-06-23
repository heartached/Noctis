using System.Net;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class WebRemoteServerTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.1.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("192.168.1.42", true)]
    [InlineData("169.254.10.10", true)]   // link-local
    [InlineData("172.32.0.1", false)]     // just outside 172.16/12
    [InlineData("8.8.8.8", false)]
    [InlineData("203.0.113.7", false)]
    public void IsPrivateAddress_ClassifiesIPv4(string ip, bool expected)
    {
        Assert.Equal(expected, WebRemoteServer.IsPrivateAddress(IPAddress.Parse(ip)));
    }

    [Fact]
    public void IsPrivateAddress_HandlesLoopbackV6_AndMappedV4()
    {
        Assert.True(WebRemoteServer.IsPrivateAddress(IPAddress.IPv6Loopback));
        Assert.True(WebRemoteServer.IsPrivateAddress(IPAddress.Parse("::ffff:192.168.0.10")));
        Assert.False(WebRemoteServer.IsPrivateAddress(IPAddress.Parse("::ffff:8.8.8.8")));
        Assert.False(WebRemoteServer.IsPrivateAddress(null));
    }
}
