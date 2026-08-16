using System.Net;
using System.Net.Sockets;
using System.Text;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class WebRemoteServerTests
{
    private static WebRemoteServer CreateServer() => new(new PlayerViewModel(
        new FakeAudioPlayer(), new FakeLibraryService(),
        new TestPersistenceService(), new FakeAnimatedCoverService()));

    private static async Task<string?> SendRequestAsync(int port, string target)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var request = $"GET {target} HTTP/1.1\r\nHost: x\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream);
        return await reader.ReadLineAsync();
    }

    [Fact]
    public async Task AuthorizedRequest_Returns200_AndRaisesClientConnected()
    {
        using var server = CreateServer();
        server.Start(0);
        var connected = new TaskCompletionSource();
        server.ClientConnected += (_, _) => connected.TrySetResult();

        var statusLine = await SendRequestAsync(server.Port, $"/?k={server.Token}");

        Assert.Contains("200", statusLine);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WrongToken_Returns403_WithoutClientConnected()
    {
        using var server = CreateServer();
        server.Start(0);
        var raised = false;
        server.ClientConnected += (_, _) => raised = true;

        var statusLine = await SendRequestAsync(server.Port, "/?k=wrong");

        Assert.Contains("403", statusLine);
        Assert.False(raised);
    }

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
