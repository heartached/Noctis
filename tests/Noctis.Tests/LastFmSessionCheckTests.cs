using System.Net;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The startup session check (LastFmService.Configure) dropped the stored session key on ANY
/// failure, offline included; SettingsViewModel then saved the empty key, signing the user out
/// of Last.fm for good after one launch without network. Only Last.fm's own "invalid session
/// key" (error 9) may forget it.
/// </summary>
public class LastFmSessionCheckTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private static async Task<LastFmService> Configure(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var service = new LastFmService(new HttpClient(new Handler(respond)));
        service.Configure("stored-key");
        await service.SessionCheck!;
        return service;
    }

    [Fact]
    public async Task Offline_KeepsTheSessionKey()
    {
        var s = await Configure(_ => throw new HttpRequestException("offline"));
        Assert.Equal("stored-key", s.GetSessionKey());
        Assert.True(s.IsAuthenticated);
    }

    [Fact]
    public async Task ServerError_KeepsTheSessionKey()
    {
        var s = await Configure(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>") });
        Assert.Equal("stored-key", s.GetSessionKey());
    }

    [Fact]
    public async Task InvalidSessionKey_ForgetsIt()
    {
        var s = await Configure(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"error\":9,\"message\":\"Invalid session key - Please re-authenticate\"}"),
        });
        Assert.Null(s.GetSessionKey());
        Assert.False(s.IsAuthenticated);
    }

    [Fact]
    public async Task ValidKey_ResolvesTheAccountName()
    {
        string? resolved = null;
        var service = new LastFmService(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"user\":{\"name\":\"tester\"}}"),
        })));
        service.UsernameResolved += (_, n) => resolved = n;
        service.Configure("stored-key");
        await service.SessionCheck!;
        Assert.Equal("tester", service.Username);
        Assert.Equal("tester", resolved);
        Assert.Equal("stored-key", service.GetSessionKey());
    }

    [Theory]
    [InlineData("{\"error\":9,\"message\":\"x\"}", true)]
    [InlineData("{\"error\":11,\"message\":\"Service Offline\"}", false)]
    [InlineData("{\"user\":{}}", false)]
    [InlineData("<html>", false)]
    [InlineData("", false)]
    public void DeadSessionReply_IsErrorNineOnly(string body, bool dead)
        => Assert.Equal(dead, LastFmService.IsDeadSessionReply(body));
}
