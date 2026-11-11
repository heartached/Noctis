using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #222: Last.fm as the Statistics page's source. The reads against canned replies shaped
/// like Last.fm's JSON (no network), the 1 h cache, the page's period mapping, library linking,
/// and the page: offered only while connected, the library's figures by default, nothing written
/// to local play counts, a read stopped when the page closes.
/// </summary>
public class LastFmStatsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public LastFmStatsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string CachePath => Path.Combine(_dir, "cache", "lastfm_stats.json");

    // ── Canned replies ──

    private const string UserInfoJson = """
        {"user":{"name":"tester","playcount":"54189","registered":{"unixtime":"1037793040","#text":1037793040}}}
        """;

    private const string TopArtistsJson = """
        {"topartists":{"artist":[
          {"name":"Daft Punk","playcount":"300","url":"https://www.last.fm/music/Daft+Punk"},
          {"name":"Nobody Here","playcount":"100"}
        ],"@attr":{"user":"tester","page":"1","perPage":"6","totalPages":"70","total":"417"}}}
        """;

    private const string TopAlbumsJson = """
        {"topalbums":{"album":[
          {"name":"Homework","playcount":"40","artist":{"name":"Daft Punk"}},
          {"name":"Unowned Record","playcount":"20","artist":{"name":"Nobody Here"}}
        ],"@attr":{"user":"tester","total":"900"}}}
        """;

    private const string TopTracksJson = """
        {"toptracks":{"track":[
          {"name":"Da Funk","playcount":"25","artist":{"name":"Daft Punk"}},
          {"name":"Ghost Song","playcount":"5","artist":{"name":"Nobody Here"}}
        ],"@attr":{"user":"tester","total":"2100"}}}
        """;

    private static string RecentJson(long total) =>
        "{\"recenttracks\":{\"track\":[],\"@attr\":{\"user\":\"tester\",\"page\":\"1\",\"perPage\":\"1\",\"total\":\"" + total + "\"}}}";

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static HttpResponseMessage Account(HttpRequestMessage r)
    {
        var q = r.RequestUri!.Query;
        if (q.Contains("method=user.getinfo")) return Reply(UserInfoJson);
        if (q.Contains("method=user.gettopartists")) return Reply(TopArtistsJson);
        if (q.Contains("method=user.gettopalbums")) return Reply(TopAlbumsJson);
        if (q.Contains("method=user.gettoptracks")) return Reply(TopTracksJson);
        if (q.Contains("method=user.getrecenttracks"))
        {
            // The window ending now has 120 scrobbles, the one before it 80.
            var to = Now.ToUnixTimeSeconds();
            return Reply(RecentJson(q.Contains("to=" + to) ? 120 : 80));
        }
        return Reply("{\"error\":3,\"message\":\"Invalid method\"}");
    }

    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    // ── The reads ──

    [Fact]
    public async Task Fetch_SevenDays_ReadsChartsTotalsAndBothWindows_WithNoSessionKey()
    {
        var handler = new Handler(Account);
        var client = new LastFmTasteClient(new HttpClient(handler), NoDelay);

        var s = await LastFmStatsClient.FetchAsync(client, "tester", LastFmPeriod.SevenDays, Now, CancellationToken.None);

        Assert.Equal(6, handler.Requests.Count);
        Assert.All(handler.Requests, u => Assert.DoesNotContain("sk=", u.Query));
        Assert.All(handler.Requests.Where(u => u.Query.Contains("gettop")), u => Assert.Contains("period=7day", u.Query));
        Assert.Contains(handler.Requests, u => u.Query.Contains("method=user.gettopartists") && u.Query.Contains("limit=6"));
        Assert.Contains(handler.Requests, u => u.Query.Contains("method=user.gettopalbums") && u.Query.Contains("limit=8"));

        var to = Now.ToUnixTimeSeconds();
        var from = Now.AddDays(-7).ToUnixTimeSeconds();
        var before = Now.AddDays(-14).ToUnixTimeSeconds();
        Assert.Contains(handler.Requests, u => u.Query.Contains($"from={from}&to={to}&limit=1"));
        Assert.Contains(handler.Requests, u => u.Query.Contains($"from={before}&to={from}&limit=1"));

        Assert.Equal(120, s.Scrobbles);
        Assert.Equal(80, s.PreviousScrobbles);
        Assert.Equal(417, s.ArtistCount);
        Assert.Equal(2100, s.TrackCount);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1037793040), s.RegisteredUtc);
        Assert.Equal(new[] { "Daft Punk", "Nobody Here" }, s.TopArtists.Select(a => a.Name));
        Assert.Equal("Homework", s.TopAlbums[0].Name);
        Assert.Equal(25, s.TopTracks[0].PlayCount);
    }

    [Fact]
    public async Task Fetch_Overall_TakesTheLifetimePlayCount_InFourReads()
    {
        var handler = new Handler(Account);
        var client = new LastFmTasteClient(new HttpClient(handler), NoDelay);

        var s = await LastFmStatsClient.FetchAsync(client, "tester", LastFmPeriod.Overall, Now, CancellationToken.None);

        Assert.Equal(4, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, u => u.Query.Contains("getrecenttracks"));
        Assert.Equal(54189, s.Scrobbles);
        Assert.Null(s.PreviousScrobbles);
    }

    [Fact]
    public async Task Fetch_PrivateAccount_IsReportedAsSuch()
    {
        var handler = new Handler(_ => Reply("{\"error\":17,\"message\":\"Login: User required to be logged in\"}", HttpStatusCode.Forbidden));
        var client = new LastFmTasteClient(new HttpClient(handler), NoDelay);

        var ex = await Assert.ThrowsAsync<LastFmTasteException>(() =>
            LastFmStatsClient.FetchAsync(client, "tester", LastFmPeriod.SevenDays, Now, CancellationToken.None));
        Assert.Equal(LastFmTasteError.Private, ex.Kind);
    }

    // ── The cache ──

    private sealed class FakeFetch
    {
        public int Calls;
        public LastFmTasteError? Fail;
        public TaskCompletionSource? Gate;
        public CancellationToken LastToken;

        public async Task<LastFmStatsSnapshot> Run(string user, string period, DateTimeOffset at, CancellationToken ct)
        {
            Calls++;
            LastToken = ct;
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            if (Fail is { } f) throw new LastFmTasteException(f);
            return new LastFmStatsSnapshot
            {
                User = user, Period = period, FetchedUtc = at,
                Scrobbles = 120, PreviousScrobbles = 80, ArtistCount = 2, TrackCount = 2,
                TopArtists = { new LastFmTasteArtist("Daft Punk", 300, null), new LastFmTasteArtist("Nobody Here", 100, null) },
                TopAlbums = { new LastFmTasteAlbum("Daft Punk", "Homework", 40, null), new LastFmTasteAlbum("Nobody Here", "Unowned Record", 20, null) },
                TopTracks = { new LastFmTasteTrack("Daft Punk", "Da Funk", 25, null), new LastFmTasteTrack("Nobody Here", "Ghost Song", 5, null) },
            };
        }
    }

    [Fact]
    public async Task Service_ServesTheCacheWithinTheHour_PerPeriod_AndFromDisk()
    {
        var now = Now;
        var fetch = new FakeFetch();
        var svc = new LastFmStatsService(CachePath, fetch.Run, () => now);

        Assert.False((await svc.GetAsync("tester", LastFmPeriod.SevenDays)).FromCache);
        Assert.True((await svc.GetAsync("tester", LastFmPeriod.SevenDays)).FromCache);
        Assert.Equal(1, fetch.Calls);
        await svc.GetAsync("tester", LastFmPeriod.Overall);
        Assert.Equal(2, fetch.Calls); // another period, another read

        // A new run reads the file: both periods, no request.
        var reopened = new LastFmStatsService(CachePath, fetch.Run, () => now);
        Assert.NotNull(await reopened.PeekAsync("tester", LastFmPeriod.Overall));
        Assert.True((await reopened.GetAsync("tester", LastFmPeriod.SevenDays)).FromCache);
        Assert.Equal(2, fetch.Calls);

        now = now.AddMinutes(61);
        Assert.False((await reopened.GetAsync("tester", LastFmPeriod.SevenDays)).FromCache);
        Assert.Equal(3, fetch.Calls);
    }

    [Fact]
    public async Task Service_FailedRefresh_KeepsTheOldFigures_AndCoolsDown()
    {
        var now = Now;
        var fetch = new FakeFetch();
        var svc = new LastFmStatsService(CachePath, fetch.Run, () => now);
        var first = (await svc.GetAsync("tester", LastFmPeriod.SevenDays)).Snapshot;

        now = now.AddHours(2);
        fetch.Fail = LastFmTasteError.Unreachable;
        var failed = await svc.GetAsync("tester", LastFmPeriod.SevenDays);
        Assert.Same(first, failed.Snapshot);
        Assert.Equal(LastFmTasteError.Unreachable, failed.Error);

        await svc.GetAsync("tester", LastFmPeriod.SevenDays); // cooling down: no request
        Assert.Equal(2, fetch.Calls);
        await svc.GetAsync("tester", LastFmPeriod.SevenDays, forceRefresh: true); // Try Again
        Assert.Equal(3, fetch.Calls);
    }

    [Fact]
    public async Task Service_AnotherAccount_ReplacesTheCache_AndClearDeletesIt()
    {
        var fetch = new FakeFetch();
        var svc = new LastFmStatsService(CachePath, fetch.Run, () => Now);
        await svc.GetAsync("tester", LastFmPeriod.SevenDays);
        await svc.GetAsync("other", LastFmPeriod.Overall);

        Assert.Null(await svc.PeekAsync("tester", LastFmPeriod.SevenDays));
        Assert.NotNull(await svc.PeekAsync("other", LastFmPeriod.Overall));

        await svc.ClearAsync();
        Assert.False(File.Exists(CachePath));
        Assert.Null(await svc.PeekAsync("other", LastFmPeriod.Overall));
    }

    [Theory]
    [InlineData(ListeningPeriod.Last7Days, "7day")]
    [InlineData(ListeningPeriod.Last30Days, "1month")]
    [InlineData(ListeningPeriod.AllTime, "overall")]
    [InlineData(ListeningPeriod.ThisYear, null)]
    public void PagePeriods_MapToLastFmPeriods_ThisYearHasNone(ListeningPeriod period, string? expected)
        => Assert.Equal(expected, StatisticsViewModel.ToLastFmPeriod(period));

    // ── Library linking ──

    private static (FakeLibraryService Lib, Track DaFunk, Album Homework) Library()
    {
        var lib = new FakeLibraryService();
        var homework = new Album { Id = Guid.NewGuid(), Name = "Homework", Artist = "Daft Punk", ArtworkPath = "C:/art/homework.jpg" };
        ((List<Album>)lib.Albums).Add(homework);
        lib.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Daft Punk" });
        var daFunk = new Track
        {
            Title = "Da Funk", Artist = "Daft Punk", AlbumArtist = "Daft Punk", Album = "Homework",
            AlbumId = homework.Id, AlbumArtworkPath = "C:/art/homework.jpg", PlayCount = 3,
            FilePath = "C:/m/Daft Punk/Homework/Da Funk.flac",
        };
        lib.TrackList.Add(daFunk);
        return (lib, daFunk, homework);
    }

    [Fact]
    public async Task Build_LinksWhatTheLibraryHas_AndShowsTheRestPlainly()
    {
        var (lib, _, homework) = Library();
        var snapshot = await new FakeFetch().Run("tester", LastFmPeriod.SevenDays, Now, CancellationToken.None);

        var r = StatisticsViewModel.BuildLastFm(snapshot, ListeningPeriod.Last7Days, lib.Tracks, lib.Albums, lib.Artists, null, Now.LocalDateTime);

        Assert.True(r.TopArtists[0].IsLinked);
        Assert.False(r.TopArtists[1].IsLinked);
        Assert.Null(r.TopArtists[1].ImagePath);

        Assert.True(r.TopAlbums[0].IsLinked);
        Assert.Equal(homework.Id, r.TopAlbums[0].AlbumId);
        Assert.Equal("C:/art/homework.jpg", r.TopAlbums[0].ArtworkPath);
        Assert.False(r.TopAlbums[1].IsLinked);
        Assert.Equal(Guid.Empty, r.TopAlbums[1].AlbumId);
        Assert.Null(r.TopAlbums[1].ArtworkPath);
        Assert.Equal("Unowned Record", r.TopAlbums[1].Title);

        Assert.True(r.TopTracks[0].IsLinked);
        Assert.Equal(homework.Id, r.TopTracks[0].AlbumId);
        Assert.False(r.TopTracks[1].IsLinked);
        Assert.Equal("Ghost Song", r.TopTracks[1].Title);

        // Scrobbles and the change from the week before; no listening time or streak invented.
        Assert.Equal(120.ToString("N0"), r.PlaysText);
        Assert.True(r.HasDelta && r.IsDeltaUp);
        Assert.Equal("—", r.ListeningTimeText);
        Assert.Equal("—", r.StreakText);
        Assert.Empty(r.HistoryRows);
    }

    // ── The page ──

    private sealed class Page
    {
        public required StatisticsViewModel Vm;
        public required SettingsViewModel Settings;
        public required FakeLibraryService Lib;
        public required FakeFetch Fetch;
        public required Track DaFunk;
        public required PlayHistoryEvent[] Events;
    }

    private Page BuildPage()
    {
        var (lib, daFunk, _) = Library();
        var events = new[] { new PlayHistoryEvent { TrackId = daFunk.Id, Title = daFunk.Title, Artist = daFunk.Artist, PlayedAtUtc = DateTime.UtcNow.AddDays(-1) } };
        var settings = new SettingsViewModel(new TestPersistenceService(), lib, new FakeHistoryLog());
        var fetch = new FakeFetch();
        var svc = new LastFmStatsService(CachePath, fetch.Run);
        var vm = new StatisticsViewModel(lib, new StatisticsNumbersTests.History(events), settings: settings, lastFmStats: svc);
        return new Page { Vm = vm, Settings = settings, Lib = lib, Fetch = fetch, DaFunk = daFunk, Events = events };
    }

    private static void Connect(SettingsViewModel settings)
    {
        settings.LastFmUsername = "tester";
        settings.IsLastFmConnected = true;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void NotConnected_NoChoice_AndTheLibraryFigures()
    {
        var p = BuildPage();
        p.Settings.StatisticsSource = StatisticsViewModel.SourceLastFm; // chosen earlier, account since gone
        StatisticsNumbersTests.Refresh(p.Vm);

        Assert.False(p.Vm.IsLastFmSourceAvailable);
        Assert.False(p.Vm.IsLastFmSource);
        Assert.Equal("1", p.Vm.PlaysText);
        Assert.Equal(0, p.Fetch.Calls);
    }

    [AvaloniaFact]
    public void Connected_LibraryIsTheDefault_LastFmIsChosen_Persisted_AndNothingLocalChanges()
    {
        var p = BuildPage();
        Connect(p.Settings);
        StatisticsNumbersTests.Refresh(p.Vm);

        Assert.True(p.Vm.IsLastFmSourceAvailable);
        Assert.True(p.Vm.IsLibrarySourceSelected);
        Assert.Equal("1", p.Vm.PlaysText);
        Assert.Equal(0, p.Fetch.Calls);

        p.Vm.IsLastFmSourceSelected = true;
        Assert.Equal(StatisticsViewModel.SourceLastFm, p.Settings.StatisticsSource);
        StatisticsNumbersTests.Refresh(p.Vm);

        Assert.True(p.Vm.IsLastFmSource);
        Assert.Equal(120.ToString("N0"), p.Vm.PlaysText);
        Assert.Equal("Daft Punk", p.Vm.TopArtists[0].Name);
        Assert.True(p.Vm.TopArtists[0].IsLinked);
        Assert.False(p.Vm.HasLastFmStatus);
        Assert.Equal(1, p.Fetch.Calls);
        Assert.Equal(3, p.DaFunk.PlayCount);   // local play counts untouched
        Assert.Single(p.Events);

        // What Last.fm can't answer is not offered.
        p.Vm.IsThisYearSelected = true;
        Assert.NotEqual(ListeningPeriod.ThisYear, p.Vm.Period);
        p.Vm.IsHistoryTabSelected = true;
        Assert.True(p.Vm.IsOverviewTabSelected);

        // A linked album row opens the library album; a plain one does nothing.
        Album? opened = null;
        p.Vm.AlbumOpened += (_, a) => opened = a;
        p.Vm.OpenRankRowCommand.Execute(p.Vm.TopAlbums[1]);
        Assert.Null(opened);
        p.Vm.OpenRankRowCommand.Execute(p.Vm.TopAlbums[0]);
        Assert.Equal("Homework", opened?.Name);

        // Back to the library.
        p.Vm.IsLibrarySourceSelected = true;
        StatisticsNumbersTests.Refresh(p.Vm);
        Assert.Equal("1", p.Vm.PlaysText);
        Assert.Equal(StatisticsViewModel.SourceLibrary, p.Settings.StatisticsSource);
    }

    [AvaloniaFact]
    public void ThisYear_SwitchingToLastFm_MovesToThirtyDays()
    {
        var p = BuildPage();
        Connect(p.Settings);
        p.Vm.Period = ListeningPeriod.ThisYear;
        p.Vm.IsHistoryTabSelected = true;
        p.Vm.IsLastFmSourceSelected = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ListeningPeriod.Last30Days, p.Vm.Period);
        Assert.True(p.Vm.IsOverviewTabSelected);
    }

    [AvaloniaFact]
    public void Offline_WithNothingSaved_SaysSo_ShowsNoNumbers_AndOffersTryAgain()
    {
        var p = BuildPage();
        Connect(p.Settings);
        p.Settings.StatisticsSource = StatisticsViewModel.SourceLastFm;
        p.Fetch.Fail = LastFmTasteError.Unreachable;
        StatisticsNumbersTests.Refresh(p.Vm);

        Assert.Equal(Noctis.Localization.Loc.T("Stats.LastFm.Unreachable"), p.Vm.LastFmStatusText);
        Assert.True(p.Vm.CanRetryLastFm);
        Assert.False(p.Vm.HasPlayHistory);
        Assert.False(p.Vm.IsEmptyNoteVisible);
        Assert.Empty(p.Vm.TopArtists);

        p.Fetch.Fail = null;
        p.Vm.RetryLastFmCommand.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        // The lists land one UI job before the status line clears: wait for both.
        while ((p.Vm.TopArtists.Count == 0 || p.Vm.HasLastFmStatus) && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(2); }
        Assert.Equal(2, p.Fetch.Calls); // Try Again skips the cool-down
        Assert.False(p.Vm.HasLastFmStatus);
        Assert.NotEmpty(p.Vm.TopArtists);
    }

    [AvaloniaFact]
    public void LeavingThePage_CancelsTheRead()
    {
        var p = BuildPage();
        Connect(p.Settings);
        p.Settings.StatisticsSource = StatisticsViewModel.SourceLastFm;
        p.Fetch.Gate = new TaskCompletionSource();
        var task = p.Vm.RefreshAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (p.Fetch.Calls == 0 && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(2); }
        Assert.Equal(1, p.Fetch.Calls);
        Assert.True(p.Vm.IsLastFmLoading);

        p.Vm.OnPageLeft();
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(2); }
        Assert.True(task.IsCompleted);
        Assert.True(p.Fetch.LastToken.IsCancellationRequested);
        Assert.False(p.Vm.IsLastFmLoading);
    }

    [AvaloniaFact]
    public void View_InLastFmMode_ShowsTheSourcePill_AndDisablesWhatLastFmCantAnswer()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("HeartFillIcon", null, out _))
        {
            app.Resources["InterSemiBold"] = FontFamily.Default;
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
            app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
        }
        var p = BuildPage();
        var view = new StatisticsView { DataContext = p.Vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        StatisticsNumbersTests.Refresh(p.Vm);
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.FindControl<Border>("SourceWell")!.IsVisible);

        Connect(p.Settings);
        p.Vm.IsLastFmSourceSelected = true;
        StatisticsNumbersTests.Refresh(p.Vm);
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.FindControl<Border>("SourceWell")!.IsVisible);
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Daft Punk", texts);
        Assert.Contains("Unowned Record", texts);
        Assert.Contains(Noctis.Localization.Loc.T("Stats.LastFm.NoListeningTime"), texts);
        // This Year and Play History are switched off, nothing else.
        Assert.Equal(2, view.GetVisualDescendants().OfType<RadioButton>().Count(r => !r.IsEnabled));
        window.Close();
    }
}
