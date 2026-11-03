using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #139: opening Settings "skips a beat" in the audio. Every .NET GC suspends the audio
/// render thread too, so what Settings allocates on open is what the music hears. This
/// measures the open path (first build of the view, the refreshes MainWindowViewModel.
/// OpenSettings starts, the version manager's release fetch) against a fake GitHub and prints
/// allocations, collections, GC pause and wall time per phase.
/// </summary>
[Collection("AutoUpdate mode")]
public class SettingsOpenCostTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public SettingsOpenCostTests(ITestOutputHelper o) => _o = o;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 15000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "condition not reached in time");
    }

    /// <summary>Allocation / GC / wall-time delta of one phase, printed as one line.</summary>
    private sealed class Probe
    {
        private readonly long _bytes = GC.GetTotalAllocatedBytes(precise: true);
        private readonly int _g0 = GC.CollectionCount(0), _g1 = GC.CollectionCount(1), _g2 = GC.CollectionCount(2);
        private readonly TimeSpan _pause = GC.GetTotalPauseDuration();
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public double AllocatedMb { get; private set; }

        public string Stop(string label)
        {
            _sw.Stop();
            AllocatedMb = (GC.GetTotalAllocatedBytes(precise: true) - _bytes) / 1048576.0;
            return $"{label}: alloc={AllocatedMb:F2} MB, gcs={GC.CollectionCount(0) - _g0}/{GC.CollectionCount(1) - _g1}/{GC.CollectionCount(2) - _g2}, " +
                   $"gcPause={(GC.GetTotalPauseDuration() - _pause).TotalMilliseconds:F1} ms, wall={_sw.Elapsed.TotalMilliseconds:F0} ms";
        }
    }

    /// <summary>
    /// The real list is ~1.47 MB of JSON (66 releases, 618 assets with uploader objects,
    /// long notes; GitHub API on 10-27). Same shape and size, generated.
    /// </summary>
    internal static string RealSizedReleasesJson(int releases = 66)
    {
        var sb = new StringBuilder(1_600_000);
        var uploader = "{\"login\":\"heartached\",\"id\":123456,\"node_id\":\"MDQ6VXNlcjEyMzQ1Ng==\",\"avatar_url\":\"https://avatars.githubusercontent.com/u/123456?v=4\",\"gravatar_id\":\"\",\"url\":\"https://api.github.com/users/heartached\",\"html_url\":\"https://github.com/heartached\",\"followers_url\":\"https://api.github.com/users/heartached/followers\",\"following_url\":\"https://api.github.com/users/heartached/following{/other_user}\",\"gists_url\":\"https://api.github.com/users/heartached/gists{/gist_id}\",\"starred_url\":\"https://api.github.com/users/heartached/starred{/owner}{/repo}\",\"subscriptions_url\":\"https://api.github.com/users/heartached/subscriptions\",\"organizations_url\":\"https://api.github.com/users/heartached/orgs\",\"repos_url\":\"https://api.github.com/users/heartached/repos\",\"events_url\":\"https://api.github.com/users/heartached/events{/privacy}\",\"received_events_url\":\"https://api.github.com/users/heartached/received_events\",\"type\":\"User\",\"user_view_type\":\"public\",\"site_admin\":false}";
        var body = string.Join("\\n", Enumerable.Range(0, 40).Select(i => $"- Fixed thing number {i} that users reported in the last release notes line"));
        sb.Append('[');
        for (var r = 0; r < releases; r++)
        {
            var tag = $"v1.{r / 10}.{r % 10}";
            if (r > 0) sb.Append(',');
            sb.Append($"{{\"url\":\"https://api.github.com/repos/heartached/Noctis/releases/{r}\",\"assets_url\":\"x\",\"upload_url\":\"x\",\"html_url\":\"https://github.com/heartached/Noctis/releases/tag/{tag}\",\"id\":{r},\"author\":{uploader},\"node_id\":\"RE_x\",\"tag_name\":\"{tag}\",\"target_commitish\":\"main\",\"name\":\"Noctis {tag}\",\"draft\":false,\"immutable\":false,\"prerelease\":false,\"created_at\":\"2026-09-01T00:00:00Z\",\"updated_at\":\"2026-09-01T00:00:00Z\",\"published_at\":\"2026-09-01T00:00:00Z\",\"assets\":[");
            for (var a = 0; a < 9; a++)
            {
                if (a > 0) sb.Append(',');
                var name = a == 0 ? $"Noctis-{tag}-Setup.exe" : a == 1 ? "SHA256SUMS.txt" : $"Noctis-{tag}-asset{a}.tar.gz";
                sb.Append($"{{\"url\":\"https://api.github.com/repos/heartached/Noctis/releases/assets/{r * 10 + a}\",\"id\":{r * 10 + a},\"node_id\":\"RA_x\",\"name\":\"{name}\",\"label\":\"\",\"uploader\":{uploader},\"content_type\":\"application/octet-stream\",\"state\":\"uploaded\",\"size\":123456789,\"digest\":\"sha256:{new string('a', 64)}\",\"download_count\":42,\"created_at\":\"2026-09-01T00:00:00Z\",\"updated_at\":\"2026-09-01T00:00:00Z\",\"browser_download_url\":\"https://github.com/heartached/Noctis/releases/download/{tag}/{name}\"}}");
            }
            sb.Append($"],\"tarball_url\":\"x\",\"zipball_url\":\"x\",\"body\":\"{body}\"}}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>GitHub's release list endpoint, counting requests; honours If-None-Match like the real API.</summary>
    internal sealed class FakeReleaseList : HttpMessageHandler
    {
        private byte[] _json = Encoding.UTF8.GetBytes(RealSizedReleasesJson());
        private string _tag = "\"releases-v1\"";
        public int Requests;
        public int NotModified;

        /// <summary>A release was published: new list, new ETag.</summary>
        public void Publish(int releases)
        {
            _json = Encoding.UTF8.GetBytes(RealSizedReleasesJson(releases));
            _tag = $"\"releases-v{releases}\"";
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            if (request.Headers.IfNoneMatch.Any(t => t.Tag == _tag && t.IsWeak))
            {
                Interlocked.Increment(ref NotModified);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }
            var content = new ByteArrayContent(_json);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(_tag, isWeak: true);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task ReleaseList_Unchanged_IsRevalidated_NotDownloadedAgain()
    {
        var api = new FakeReleaseList();
        var svc = new UpdateService(new HttpClient(api));

        var first = await svc.ListReleasesAsync();
        var second = await svc.ListReleasesAsync();

        Assert.Equal(66, first.Count);
        Assert.Equal(2, api.Requests);
        Assert.Equal(1, api.NotModified); // second asked with If-None-Match and got 304
        Assert.Equal(first.Select(r => r.Info.TagName), second.Select(r => r.Info.TagName));
        Assert.NotSame(first, second); // callers own their list
    }

    [Fact]
    public async Task ReleaseList_NewRelease_IsDownloadedAndParsed()
    {
        var api = new FakeReleaseList();
        var svc = new UpdateService(new HttpClient(api));
        await svc.ListReleasesAsync();

        api.Publish(releases: 67);
        var after = await svc.ListReleasesAsync();
        var again = await svc.ListReleasesAsync();

        Assert.Equal(67, after.Count);
        Assert.Equal(67, again.Count);
        Assert.Equal(1, api.NotModified); // only the last one
    }

    [Fact]
    public async Task ReadJsonBounded_UnannouncedOversizeBody_Throws()
    {
        // No Content-Length (chunked): the cap has to hold while streaming.
        var json = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Repeat("\"0123456789\"", 500)) + "]");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            HttpSafety.ReadJsonBoundedAsync<List<string>>(new NoLengthContent(json), maxBytes: 1024));
        var ok = await HttpSafety.ReadJsonBoundedAsync<List<string>>(new NoLengthContent(json), maxBytes: json.Length);
        Assert.Equal(500, ok!.Count);
    }

    /// <summary>Body without a Content-Length header, like a chunked response.</summary>
    private sealed class NoLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private async Task<(SettingsViewModel Vm, FakeLibraryService Library)> CreateVmAsync()
    {
        var library = new FakeLibraryService();
        for (var i = 0; i < 3; i++)
            library.TrackList.Add(new Track { Id = Guid.NewGuid(), Title = $"Song {i}", FilePath = $"/music/{i}.flac" });
        var vm = new SettingsViewModel(new PersistenceService(_root), library, new FakePlayHistory());
        await vm.LoadAsync();
        return (vm, library);
    }

    private static Window Show(SettingsView view)
    {
        var window = new Window { Width = 1200, Height = 800, Content = view, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        Pump(50);
        return window;
    }

    [AvaloniaFact]
    public async Task FirstOpen_OnlyTheShownTabPage_JoinsTheTree()
    {
        EnsureAppStyles();
        var (vm, _) = await CreateVmAsync();
        var view = new SettingsView { DataContext = vm };
        var window = Show(view);

        Assert.True(view.GeneralTabPanel.IsEffectivelyVisible);
        Assert.Null(view.AudioTabPanel.Parent); // built (named, wired) but not styled or bound yet
        Assert.Null(view.AboutTabPanel.Parent);
        Assert.Equal(SettingsView.TabPanels.Length - 1, view.DeferredTabsForTests.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TabFirstVisit_PutsItsPageBackInItsSlot_AndGlidesIn()
    {
        EnsureAppStyles();
        var (vm, _) = await CreateVmAsync();
        var view = new SettingsView { DataContext = vm };
        var window = Show(view);
        var host = (Panel)view.GeneralTabPanel.Parent!;

        vm.SelectedSettingsTab = SettingsViewModel.TabAbout;
        vm.SelectedSettingsTab = SettingsViewModel.TabAudio;
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render); // before the Loaded-priority release
        Assert.Same(host, view.AudioTabPanel.Parent);
        Assert.Equal(0, view.AudioTabPanel.Opacity); // enters from the hidden tab-page state
        Pump(400);

        // Original XAML order, whatever order the tabs were visited in.
        var pages = host.Children.Where(c => c.Classes.Contains("tab-page")).ToList();
        Assert.Equal(new Control[] { view.GeneralTabPanel, view.AudioTabPanel, view.AboutTabPanel }, pages);
        Assert.True(view.AudioTabPanel.IsEffectivelyVisible);
        Assert.False(view.AboutTabPanel.IsVisible);
        Assert.Equal(1, view.AudioTabPanel.Opacity, 2);

        // Visited pages stay: going back costs nothing.
        vm.SelectedSettingsTab = SettingsViewModel.TabGeneral;
        Pump(50);
        Assert.Same(host, view.AudioTabPanel.Parent);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Search_RealizesEveryPage_AndFindsCardsOnUnvisitedTabs()
    {
        EnsureAppStyles();
        var (vm, _) = await CreateVmAsync();
        var view = new SettingsView { DataContext = vm };
        var window = Show(view);

        view.SettingsSearchBox.Text = "Developer";
        Pump(50);

        Assert.Empty(view.DeferredTabsForTests);
        Assert.True(vm.Sections.Single(s => s.Key == SettingsViewModel.TabAdvanced).MatchCount > 0);
        Assert.Equal(SettingsViewModel.TabAdvanced, vm.SelectedSettingsTab);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Open_RefreshesOnlyTheShownTab()
    {
        var (vm, library) = await CreateVmAsync();
        library.TrackList[0].SnoozedUntil = DateTime.UtcNow.AddDays(1);

        vm.RefreshShownTabData(); // opens on General: no statistics or library pass
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, vm.TotalSongs);
        Assert.Empty(vm.SnoozedTracks);

        vm.SelectedSettingsTab = SettingsViewModel.TabLibrary;
        Assert.Single(vm.SnoozedTracks);

        vm.SelectedSettingsTab = SettingsViewModel.TabStatistics;
        await PumpUntil(() => vm.TotalSongs == 3);
    }

    /// <summary>
    /// Prints each phase; asserts nothing about byte counts (those vary with the machine and
    /// Avalonia version). Run with -c Release and console verbosity=detailed to read them.
    /// </summary>
    [AvaloniaFact]
    public async Task SettingsOpen_PhaseCosts()
    {
        EnsureAppStyles();
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings { DeveloperMode = true });
        var library = new FakeLibraryService();
        for (var i = 0; i < 2000; i++)
            library.TrackList.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"Song {i}", Artist = $"Artist {i % 150}", Album = $"Album {i % 180}",
                FilePath = $"/music/{i}.flac", Duration = TimeSpan.FromMinutes(3.5), FileSize = 30_000_000,
            });
        var history = new FakePlayHistory();
        for (var i = 0; i < 20_000; i++)
        {
            var t = library.TrackList[i % 2000];
            history.EventList.Add(new PlayHistoryEvent { TrackId = t.Id, Title = t.Title, Artist = t.Artist, PlayedAtUtc = DateTime.UtcNow.AddMinutes(-i * 4) });
        }
        await persistence.SavePlaylistsAsync(Enumerable.Range(0, 30)
            .Select(i => new Playlist { Name = $"List {i}", TrackIds = library.TrackList.Skip(i * 50).Take(100).Select(t => t.Id).ToList() })
            .ToList());

        var api = new FakeReleaseList();
        var vm = new SettingsViewModel(persistence, library, history);
        vm.SetUpdateService(new UpdateService(new HttpClient(api)));

        // Warm-up: JIT the fetch and stats paths once so the numbers below are steady-state.
        await new UpdateService(new HttpClient(new FakeReleaseList())).ListReleasesAsync();
        await vm.RefreshLibraryStatsAsync();

        var p = new Probe();
        await vm.LoadAsync(); // Developer Mode on: the launch fetch
        await PumpUntil(() => vm.DevReleases.Count > 0);
        _o.WriteLine(p.Stop("launch: LoadAsync + release list fetch") + $", requests={api.Requests}");

        using var ticks = new AllocTicks();
        var before = api.Requests;
        p = new Probe();
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 800, Content = view, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        Pump(300);
        _o.WriteLine(p.Stop("first open: build + attach + layout (General)") + $", release requests={api.Requests - before}");
        ticks.Dump(_o, "first open", 8);

        p = new Probe();
        vm.RefreshShownTabData(); // what OpenSettings does
        Pump(150);
        _o.WriteLine(p.Stop("every open: OpenSettings refreshes (2,000 tracks, 20,000 plays, 30 playlists)"));
        ticks.Dump(_o, "refreshes", 8);

        p = new Probe();
        vm.SelectedSettingsTab = SettingsViewModel.TabAudio;
        Pump(200);
        _o.WriteLine(p.Stop("Audio tab, first visit"));
        ticks.Dump(_o, "audio", 8);

        before = api.Requests;
        p = new Probe();
        vm.SelectedSettingsTab = SettingsViewModel.TabAbout;
        await PumpUntil(() => !vm.IsLoadingReleases);
        Pump(100);
        _o.WriteLine(p.Stop("About tab (release list re-fetch)") + $", requests={api.Requests - before}, notModified={api.NotModified}");
        ticks.Dump(_o, "about", 8);

        window.Close();
    }

    /// <summary>Runtime AllocationTick events (one per ~100 KB allocated), summed by type.</summary>
    private sealed class AllocTicks : System.Diagnostics.Tracing.EventListener
    {
        private readonly Dictionary<string, long> _byType = new();

        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
        }

        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (e.EventName != "GCAllocationTick_V4" && e.EventName != "GCAllocationTick_V3") return;
            var i = e.PayloadNames!.IndexOf("TypeName");
            var amt = e.PayloadNames.IndexOf("AllocationAmount64");
            var name = i >= 0 ? e.Payload![i] as string ?? "?" : "?";
            var bytes = amt >= 0 ? Convert.ToInt64(e.Payload![amt]) : 100_000;
            lock (_byType) _byType[name] = _byType.GetValueOrDefault(name) + bytes;
        }

        public void Dump(ITestOutputHelper o, string label, int top)
        {
            lock (_byType)
            {
                foreach (var (k, v) in _byType.OrderByDescending(kv => kv.Value).Take(top))
                    o.WriteLine($"    [{label}] {v / 1048576.0:F2} MB  {k}");
                _byType.Clear();
            }
        }
    }

    private sealed class FakePlayHistory : IPlayHistoryService
    {
        public List<PlayHistoryEvent> EventList { get; } = new();
        public IReadOnlyList<PlayHistoryEvent> Events => EventList;
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
