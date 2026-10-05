using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #118: the ListenBrainz API URL is configurable so listens can go to a self-hosted
/// ListenBrainz-compatible server (Koito documents "&lt;host&gt;/apis/listenbrainz" with or
/// without "/1"). Blank keeps the official api.listenbrainz.org.
/// </summary>
public class ListenBrainzApiUrlTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Records every request; answers with whatever <c>Respond</c> returns.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Auth)> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => ValidJson("alice");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage ValidJson(string user) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $"{{\"code\":200,\"message\":\"Token valid.\",\"valid\":true,\"user_name\":\"{user}\"}}",
            Encoding.UTF8, "application/json"),
    };

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static Track Song() => new() { Id = Guid.NewGuid(), Title = "Song", Artist = "Artist", Duration = TimeSpan.FromMinutes(3) };

    // ── Normalization ──

    [Theory]
    [InlineData(null, "https://api.listenbrainz.org")]
    [InlineData("", "https://api.listenbrainz.org")]
    [InlineData("   ", "https://api.listenbrainz.org")]
    [InlineData("https://api.listenbrainz.org", "https://api.listenbrainz.org")]
    [InlineData("https://api.listenbrainz.org/", "https://api.listenbrainz.org")]
    [InlineData("https://api.listenbrainz.org/1", "https://api.listenbrainz.org")]
    [InlineData("https://api.listenbrainz.org/1/", "https://api.listenbrainz.org")]
    [InlineData("  https://koito.example.com/apis/listenbrainz  ", "https://koito.example.com/apis/listenbrainz")]
    [InlineData("https://koito.example.com/apis/listenbrainz/", "https://koito.example.com/apis/listenbrainz")]
    [InlineData("https://koito.example.com/apis/listenbrainz/1", "https://koito.example.com/apis/listenbrainz")]
    [InlineData("https://koito.example.com/apis/listenbrainz/1/", "https://koito.example.com/apis/listenbrainz")]
    [InlineData("https://koito.example.com/apis/listenbrainz/1/submit-listens", "https://koito.example.com/apis/listenbrainz")]
    [InlineData("https://koito.example.com/apis/listenbrainz/1/validate-token?x=1", "https://koito.example.com/apis/listenbrainz")]
    // Plain http is kept as typed, LAN or not — self-hosted scrobblers often run without TLS.
    [InlineData("http://192.168.1.20:4110/apis/listenbrainz", "http://192.168.1.20:4110/apis/listenbrainz")]
    [InlineData("http://koito.example.com/apis/listenbrainz/1", "http://koito.example.com/apis/listenbrainz")]
    // No scheme: http for LAN hosts, https for public ones.
    [InlineData("192.168.1.20:4110/apis/listenbrainz", "http://192.168.1.20:4110/apis/listenbrainz")]
    [InlineData("nas:4110/apis/listenbrainz/1", "http://nas:4110/apis/listenbrainz")]
    [InlineData("koito.example.com/apis/listenbrainz", "https://koito.example.com/apis/listenbrainz")]
    // "/1" only counts as a whole trailing segment.
    [InlineData("https://scrobble.example.com/v1", "https://scrobble.example.com/v1")]
    public void NormalizeApiUrl_Cases(string? input, string expected)
        => Assert.Equal(expected, ListenBrainzService.NormalizeApiUrl(input));

    [Theory]
    [InlineData("ftp://koito.example.com")]
    [InlineData("http://")]
    [InlineData("not a url at all")]
    public void NormalizeApiUrl_RejectsNonHttpInput(string input)
        => Assert.Null(ListenBrainzService.NormalizeApiUrl(input));

    // ── Request URLs ──

    [Fact]
    public async Task DefaultUrl_IsUnchanged()
    {
        var handler = new RecordingHandler();
        var svc = new ListenBrainzService(new HttpClient(handler));
        svc.Configure("tok");

        Assert.Equal("alice", await svc.ValidateTokenAsync());
        await svc.UpdateNowPlayingAsync(Song());
        await svc.ScrobbleAsync(Song(), DateTime.UtcNow);

        Assert.Equal(new[]
        {
            "https://api.listenbrainz.org/1/validate-token",
            "https://api.listenbrainz.org/1/submit-listens",
            "https://api.listenbrainz.org/1/submit-listens",
        }, handler.Requests.Select(r => r.Url));
        Assert.All(handler.Requests, r => Assert.Equal("Token tok", r.Auth));
    }

    [Theory]
    [InlineData("http://192.168.1.20:4110/apis/listenbrainz")]
    [InlineData("http://192.168.1.20:4110/apis/listenbrainz/1/")]
    public async Task CustomUrl_IsUsedForValidateNowPlayingAndSubmit(string configured)
    {
        var handler = new RecordingHandler();
        var svc = new ListenBrainzService(new HttpClient(handler));
        svc.Configure("koito-key");
        svc.SetApiUrl(configured);

        Assert.Equal("alice", await svc.ValidateTokenAsync());
        await svc.UpdateNowPlayingAsync(Song());
        await svc.ScrobbleAsync(Song(), DateTime.UtcNow);

        Assert.Equal(new[]
        {
            (HttpMethod.Get, "http://192.168.1.20:4110/apis/listenbrainz/1/validate-token"),
            (HttpMethod.Post, "http://192.168.1.20:4110/apis/listenbrainz/1/submit-listens"),
            (HttpMethod.Post, "http://192.168.1.20:4110/apis/listenbrainz/1/submit-listens"),
        }, handler.Requests.Select(r => (r.Method, r.Url)));
    }

    [Fact]
    public void SetApiUrl_BlankOrInvalid_FallsBackToOfficial()
    {
        var svc = new ListenBrainzService(new HttpClient(new RecordingHandler()));
        svc.SetApiUrl("https://koito.example.com/apis/listenbrainz");
        svc.SetApiUrl("");
        Assert.Equal(ListenBrainzService.DefaultApiUrl, svc.ApiUrl);
        svc.SetApiUrl("ftp://nope");
        Assert.Equal(ListenBrainzService.DefaultApiUrl, svc.ApiUrl);
    }

    // ── Validation errors ──

    [Fact]
    public async Task Validate_ClassifiesFailures()
    {
        var handler = new RecordingHandler();
        var svc = new ListenBrainzService(new HttpClient(handler));
        svc.Configure("tok");

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        Assert.Null(await svc.ValidateTokenAsync());
        Assert.Equal(ListenBrainzValidationError.InvalidToken, svc.LastValidationError);

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"code\":200,\"message\":\"Token invalid.\",\"valid\":false}"),
        };
        Assert.Null(await svc.ValidateTokenAsync());
        Assert.Equal(ListenBrainzValidationError.InvalidToken, svc.LastValidationError);

        // e.g. a Koito host without "/apis/listenbrainz": the web app answers instead.
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!doctype html><html></html>", Encoding.UTF8, "text/html"),
        };
        Assert.Null(await svc.ValidateTokenAsync());
        Assert.Equal(ListenBrainzValidationError.NotCompatible, svc.LastValidationError);

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        Assert.Null(await svc.ValidateTokenAsync());
        Assert.Equal(ListenBrainzValidationError.NotCompatible, svc.LastValidationError);

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        Assert.Null(await svc.ValidateTokenAsync());
        Assert.Equal(ListenBrainzValidationError.NotCompatible, svc.LastValidationError);

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        Assert.Null(await svc.ValidateTokenAsync());
        Assert.Equal(ListenBrainzValidationError.Unreachable, svc.LastValidationError);

        handler.Respond = _ => throw new HttpRequestException("No connection could be made");
        Assert.Null(await svc.ValidateTokenAsync());
        Assert.Equal(ListenBrainzValidationError.Unreachable, svc.LastValidationError);

        handler.Respond = _ => ValidJson("alice");
        Assert.Equal("alice", await svc.ValidateTokenAsync());
        Assert.Equal(ListenBrainzValidationError.None, svc.LastValidationError);
    }

    // ── Settings ──

    [Fact]
    public void FreshInstall_UsesTheOfficialServer() => Assert.Equal("", new AppSettings().ListenBrainzApiUrl);

    private SettingsViewModel CreateSettings(IListenBrainzService listenBrainz)
    {
        var vm = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
        vm.SetListenBrainz(listenBrainz);
        return vm;
    }

    [AvaloniaFact]
    public async Task Connect_ValidatesAgainstCustomUrl_AndItSurvivesReload()
    {
        var handler = new RecordingHandler();
        var vm = CreateSettings(new ListenBrainzService(new HttpClient(handler)));
        await vm.LoadAsync();

        vm.ListenBrainzToken = "koito-key";
        vm.ListenBrainzApiUrl = "  http://192.168.1.20:4110/apis/listenbrainz/1/ ";
        await vm.TestListenBrainzCommand.ExecuteAsync(null);

        Assert.True(vm.IsListenBrainzConnected);
        Assert.Equal("http://192.168.1.20:4110/apis/listenbrainz/1/validate-token", handler.Requests.Single().Url);
        Assert.Equal("http://192.168.1.20:4110/apis/listenbrainz", vm.ListenBrainzApiUrl);
        Assert.Equal("http://192.168.1.20:4110/apis/listenbrainz", vm.ListenBrainzServerDisplay);

        // A fresh start points the service at the saved server before any scrobble.
        var handler2 = new RecordingHandler();
        var service2 = new ListenBrainzService(new HttpClient(handler2));
        var reloaded = CreateSettings(service2);
        await reloaded.LoadAsync();

        Assert.Equal("http://192.168.1.20:4110/apis/listenbrainz", reloaded.GetSettings().ListenBrainzApiUrl);
        Assert.Equal("http://192.168.1.20:4110/apis/listenbrainz", service2.ApiUrl);
        Assert.True(reloaded.IsListenBrainzConnected);
        await service2.ScrobbleAsync(Song(), DateTime.UtcNow);
        Assert.Equal("http://192.168.1.20:4110/apis/listenbrainz/1/submit-listens", handler2.Requests.Single().Url);
    }

    [AvaloniaFact]
    public async Task Connect_DefaultUrl_StaysBlankAndHitsOfficialServer()
    {
        var handler = new RecordingHandler();
        var vm = CreateSettings(new ListenBrainzService(new HttpClient(handler)));
        await vm.LoadAsync();

        vm.ListenBrainzToken = "tok";
        vm.ListenBrainzApiUrl = "https://api.listenbrainz.org/1";
        await vm.TestListenBrainzCommand.ExecuteAsync(null);

        Assert.True(vm.IsListenBrainzConnected);
        Assert.Equal("https://api.listenbrainz.org/1/validate-token", handler.Requests.Single().Url);
        Assert.Equal("", vm.ListenBrainzApiUrl);
        Assert.Equal("", vm.ListenBrainzServerDisplay);
    }

    [AvaloniaFact]
    public async Task Connect_ShowsWhyItFailed()
    {
        var handler = new RecordingHandler();
        var vm = CreateSettings(new ListenBrainzService(new HttpClient(handler)));
        await vm.LoadAsync();
        vm.ListenBrainzToken = "tok";

        vm.ListenBrainzApiUrl = "ftp://koito.example.com";
        await vm.TestListenBrainzCommand.ExecuteAsync(null);
        Assert.Equal("Enter a valid http:// or https:// URL.", vm.ListenBrainzError);
        Assert.Empty(handler.Requests);

        vm.ListenBrainzApiUrl = "http://192.168.1.20:4110";
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        await vm.TestListenBrainzCommand.ExecuteAsync(null);
        Assert.False(vm.IsListenBrainzConnected);
        Assert.Equal("No ListenBrainz-compatible API at this URL.", vm.ListenBrainzError);

        handler.Respond = _ => throw new HttpRequestException("refused");
        await vm.TestListenBrainzCommand.ExecuteAsync(null);
        Assert.Equal("Couldn't reach the server. Check the URL and your connection.", vm.ListenBrainzError);

        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        await vm.TestListenBrainzCommand.ExecuteAsync(null);
        Assert.Equal("The server rejected this token.", vm.ListenBrainzError);
    }
}
