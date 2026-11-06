using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.Services;
using Noctis.Mobile.Services.Scrobbling;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #177 on the phone's Home: From Your Last.fm (mixes of library songs only) and Discover
/// on Last.fm (artists and albums the library lacks, opening only their Last.fm pages). Shown only
/// while Last.fm is connected on the phone and Settings › Scrobbling › Mixes and Recommendations
/// is on. Canned Last.fm replies, no network.
/// </summary>
public sealed class MobileLastFmHomeTests : IDisposable
{
    private readonly List<MobileFixtures.Rig> _rigs = new();

    public void Dispose()
    {
        foreach (var rig in _rigs) rig.Dispose();
    }

    private const string TopTracksJson = """
        {"toptracks":{"track":[
          {"name":"Song A","playcount":"42","artist":{"name":"Band"}},
          {"name":"Ghost Song","playcount":"20","artist":{"name":"Nobody"}}
        ]}}
        """;

    private const string LovedJson = """
        {"lovedtracks":{"track":[{"name":"Song B","artist":{"name":"Band"}}]}}
        """;

    private const string TopArtistsJson = """
        {"topartists":{"artist":[
          {"name":"Band","playcount":"300","url":"https://www.last.fm/music/Band"},
          {"name":"Missing Artist","playcount":"100","url":"https://www.last.fm/music/Missing+Artist"}
        ]}}
        """;

    private const string TopAlbumsJson = """
        {"topalbums":{"album":[{"name":"Absent Album","playcount":"50","artist":{"name":"Missing Artist"}}]}}
        """;

    // Band (in the library) comes back as similar too: Discover must leave it out.
    private const string SimilarJson = """
        {"similarartists":{"artist":[
          {"name":"Similar One","match":"0.9","url":"https://www.last.fm/music/Similar+One"},
          {"name":"Band","match":"0.8","url":"https://www.last.fm/music/Band"}
        ]}}
        """;

    private sealed class LastFm : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var method = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["method"] ?? "";
            var body = method switch
            {
                "user.gettoptracks" => TopTracksJson,
                "user.getlovedtracks" => LovedJson,
                "user.gettopartists" => TopArtistsJson,
                "user.gettopalbums" => TopAlbumsJson,
                "artist.getsimilar" => SimilarJson,
                _ => null,
            };
            return Task.FromResult(body == null
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":3,\"message\":\"Invalid Method\"}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    /// <summary>The scrobbler never sends anything in these tests.</summary>
    private sealed class NoNet : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("no scrobbling in these tests");
    }

    private sealed class Links : IUrlLauncher
    {
        public List<string> Opened { get; } = new();
        public bool Open(string url) { Opened.Add(url); return true; }
    }

    private sealed record Setup(ShellViewModel Shell, PhoneScrobbler Scrobbler, LastFm Http, Links Links, MobileFixtures.Rig Rig, string CachePath);

    private static readonly Track SongA = MobileFixtures.Song("Song A", "Band");
    private static readonly Track SongB = MobileFixtures.Song("Song B", "Band");
    private static readonly Track Other = MobileFixtures.Song("Other", "Else");

    private async Task<Setup> MakeAsync(bool connected = true, bool tasteOn = true, bool withService = true)
    {
        var tracks = new[] { Clone(SongA), Clone(SongB), Clone(Other) };
        var rig = MobileFixtures.MakeRig(tracks, seed: p => p.UpdateSettingsAsync(s =>
        {
            s.LastFmScrobblingEnabled = connected;
            s.LastFmSessionKey = connected ? "sk" : "";
            s.LastFmUsername = connected ? "ferlin" : "";
            s.LastFmTasteEnabled = tasteOn;
        }));
        _rigs.Add(rig);
        var scrobbler = new PhoneScrobbler(rig.Persistence, new HttpClient(new NoNet())) { AutoFlush = false, HasComputerRoute = false };
        await scrobbler.LoadAsync();
        var http = new LastFm();
        var links = new Links();
        var cache = Path.Combine(rig.Root, "cache", "lastfm_taste.json");
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Scrobbler = scrobbler,
            Marshal = a => a(),
            Links = links,
            LastFmTaste = withService ? new LastFmTasteService(new HttpClient(http), cache, delay: (_, _) => Task.CompletedTask) : null,
        };
        return new Setup(shell, scrobbler, http, links, rig, cache);
    }

    private static Track Clone(Track t) => new()
    {
        Id = Guid.NewGuid(), Title = t.Title, Artist = t.Artist, AlbumArtist = t.AlbumArtist, Album = t.Album,
        FilePath = t.FilePath, Duration = t.Duration, DateAdded = t.DateAdded,
    };

    private static HashSet<Guid> LibraryIds(Setup s) => s.Rig.Library.TrackList.Select(t => t.Id).ToHashSet();

    [Fact]
    public async Task Shown_OnlyWithLastFmConnected_AndTheSwitchOn()
    {
        var on = await MakeAsync();
        await on.Shell.LastFm.RefreshAsync(force: false);
        Assert.True(on.Shell.LastFm.IsActive);
        Assert.True(on.Shell.LastFm.IsSectionVisible);
        Assert.True(on.Shell.LastFm.IsDiscoverVisible);
        Assert.False(on.Shell.LastFm.HasStatus);

        foreach (var (connected, taste, service) in new[] { (false, true, true), (true, false, true), (true, true, false) })
        {
            var off = await MakeAsync(connected, taste, service);
            await off.Shell.LastFm.RefreshAsync(force: false);
            Assert.False(off.Shell.LastFm.IsActive);
            Assert.False(off.Shell.LastFm.IsSectionVisible);
            Assert.False(off.Shell.LastFm.IsDiscoverVisible);
            Assert.Empty(off.Shell.LastFm.Mixes);
            Assert.Empty(off.Shell.LastFm.Discover);
            Assert.Equal(0, off.Http.Requests);   // nothing asked of Last.fm
        }
    }

    [Fact]
    public async Task Mixes_HoldOnlyLibrarySongs_AndATileOpensThem()
    {
        var s = await MakeAsync();
        await s.Shell.LastFm.RefreshAsync(force: false);

        var mixes = s.Shell.LastFm.Mixes;
        Assert.Equal(new[] { "Mobile.LastFm.TopSongs", "Mobile.LastFm.Loved", "Mobile.LastFm.TopArtists" },
            mixes.Select(m => ((LastFmMix)m.Payload).TitleKey));
        Assert.All(mixes, m => Assert.Equal(RailItemKind.Mix, m.Kind));
        var ids = LibraryIds(s);
        foreach (var tile in mixes)
        {
            var mix = (LastFmMix)tile.Payload;
            Assert.NotEmpty(mix.TrackIds);
            Assert.All(mix.TrackIds, id => Assert.Contains(id, ids));
        }
        // Top Songs: Song A only ("Ghost Song" is not in the library).
        var top = (LastFmMix)mixes[0].Payload;
        Assert.Equal(new[] { "Song A" }, top.TrackIds.Select(id => s.Rig.Library.GetTrackById(id)!.Title));

        s.Shell.OpenRailItemCommand.Execute(mixes[1]);
        var page = Assert.IsType<SongListPageViewModel>(s.Shell.CurrentPage);
        Assert.Equal(new[] { "Song B" }, page.Songs.Select(t => t.Title));
    }

    [Fact]
    public async Task Discover_LeavesOutLibraryArtists_AndOpensOnlyLastFmPages()
    {
        var s = await MakeAsync();
        await s.Shell.LastFm.RefreshAsync(force: false);

        var discover = s.Shell.LastFm.Discover;
        Assert.NotEmpty(discover);
        var libraryArtists = s.Rig.Library.TrackList.Select(t => t.Artist).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(discover, e =>
        {
            Assert.DoesNotContain(e.IsAlbum ? e.Item.Artist! : e.Title, libraryArtists);
            Assert.True(LastFmLinks.IsLastFmPage(e.Url), e.Url);
        });
        Assert.Contains(discover, e => e.Title == "Similar One");
        Assert.Contains(discover, e => e.IsAlbum && e.Title == "Absent Album");
        Assert.DoesNotContain(discover, e => e.Title == "Band");

        var first = discover[0];
        s.Shell.LastFm.OpenDiscoverCommand.Execute(first);
        Assert.Equal(new[] { first.Url }, s.Links.Opened);

        // Anything but a Last.fm page is refused.
        var foreign = new LastFmDiscoverEntry(first.Item with { Url = "https://example.com/buy" }, "X", "Y");
        s.Shell.LastFm.OpenDiscoverCommand.Execute(foreign);
        Assert.Single(s.Links.Opened);
    }

    [Fact]
    public async Task SettingsSwitch_HidesTheSections_AndIsSaved()
    {
        var s = await MakeAsync();
        await s.Shell.LastFm.RefreshAsync(force: false);
        Assert.True(s.Shell.LastFm.IsSectionVisible);

        var pane = new ScrobblingSettingsViewModel(s.Shell);
        Assert.True(pane.ShowTasteSwitch);
        Assert.True(pane.LastFmTaste);
        pane.LastFmTaste = false;
        for (var i = 0; i < 50 && (await s.Rig.Persistence.LoadSettingsAsync()).LastFmTasteEnabled; i++) await Task.Delay(20);   // the save runs on

        Assert.False(s.Shell.LastFm.IsActive);
        Assert.False(s.Shell.LastFm.IsSectionVisible);
        Assert.Empty(s.Shell.LastFm.Mixes);
        Assert.Empty(s.Shell.LastFm.Discover);
        Assert.False((await s.Rig.Persistence.LoadSettingsAsync()).LastFmTasteEnabled);
        pane.Detach();
    }

    [Fact]
    public async Task SettingsSwitch_HiddenWithoutLastFm()
    {
        var s = await MakeAsync(connected: false);
        var pane = new ScrobblingSettingsViewModel(s.Shell);
        Assert.False(pane.ShowTasteSwitch);
        pane.Detach();
    }

    [Fact]
    public async Task Disconnecting_HidesTheSections_AndForgetsTheSavedCharts()
    {
        var s = await MakeAsync();
        await s.Shell.LastFm.RefreshAsync(force: false);
        Assert.True(File.Exists(s.CachePath));

        await s.Scrobbler.DisconnectAsync(lastFm: true);
        for (var i = 0; i < 50 && File.Exists(s.CachePath); i++) await Task.Delay(20);

        Assert.False(s.Shell.LastFm.IsSectionVisible);
        Assert.Empty(s.Shell.LastFm.Mixes);
        Assert.False(File.Exists(s.CachePath));
    }

    [Fact]
    public async Task UnknownAccount_SaysSo()
    {
        var s = await MakeAsync();
        var shell = new ShellViewModel(s.Rig.Shell.Library, s.Rig.Shell.Player, s.Rig.Shell.Lyrics)
        {
            Scrobbler = s.Scrobbler,
            Marshal = a => a(),
            LastFmTaste = new LastFmTasteService(new HttpClient(new NotFound()), Path.Combine(s.Rig.Root, "cache", "other.json"),
                delay: (_, _) => Task.CompletedTask),
        };
        await shell.LastFm.RefreshAsync(force: false);
        Assert.True(shell.LastFm.IsSectionVisible);
        Assert.Empty(shell.LastFm.Mixes);
        Assert.Equal(Noctis.Localization.Loc.T("Mobile.LastFm.UserNotFound"), shell.LastFm.Status);
    }

    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":6,\"message\":\"User not found\"}") });
    }

    [AvaloniaFact]
    public async Task HomePage_ShowsTheMixRail_AndTheDiscoverList()
    {
        var s = await MakeAsync();
        var window = MobileFixtures.Mount(s.Shell, out var view);
        var home = MobileFixtures.Find<HomePage>(view);
        Assert.False(MobileFixtures.Named<StackPanel>(home, "LastFmSection").IsEffectivelyVisible);

        await s.Shell.LastFm.RefreshAsync(force: false);
        window.UpdateLayout();

        Assert.True(MobileFixtures.Named<StackPanel>(home, "LastFmSection").IsEffectivelyVisible);
        Assert.True(MobileFixtures.Named<StackPanel>(home, "LastFmDiscoverSection").IsEffectivelyVisible);
        Assert.Equal(3, MobileFixtures.Named<ItemsControl>(home, "LastFmRail").ItemCount);
        Assert.Equal(s.Shell.LastFm.Discover.Count, MobileFixtures.Named<ItemsControl>(home, "LastFmDiscoverList").ItemCount);
        window.Close();
    }
}
