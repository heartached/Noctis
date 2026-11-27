using Avalonia.Headless.XUnit;
using Noctis.Controls;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services.YouTube;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Music videos from YouTube, 11-25 round 3:
/// <list type="bullet">
/// <item>YouTube's "Sign in to confirm you're not a bot" is its own failure kind: no update +
/// retry, a 30 min cool-down for everything automatic (search, resolve, refresh, recovery),
/// saved choices kept, a plain message in the dialog and in Settings, and an opt-in
/// <c>--cookies-from-browser</c> (with a readable message when the cookies can't be read).</item>
/// <item>A stream that is slow to start (or to land a seek) is not a stalled one.</item>
/// </list>
/// All offline: yt-dlp is a fake runner / fake source, the decoder a fake clock.
/// </summary>
public class MusicVideoBotCheckTests : IDisposable
{
    private const string RealBotLine =
        "ERROR: [youtube] FGBhQbmPwH8: Sign in to confirm you're not a bot. Use --cookies-from-browser or --cookies for the authentication. See  https://github.com/yt-dlp/yt-dlp/wiki/FAQ#how-do-i-pass-cookies-to-yt-dlp  for how to manually pass cookies. Also see  https://github.com/yt-dlp/yt-dlp/wiki/Extractors#exporting-youtube-cookies  for tips on effectively exporting YouTube cookies";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-mvbot-" + Guid.NewGuid().ToString("N")[..8]);

    public MusicVideoBotCheckTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── Parsing ──

    [Theory]
    [InlineData(RealBotLine)]
    [InlineData("WARNING: [youtube] x\nERROR: [youtube] abc: Sign in to confirm you’re not a bot. Use --cookies-from-browser")]
    [InlineData("ERROR: [youtube] abc: Please confirm you are not a bot")]
    [InlineData("error: [youtube] abc: sign in to confirm your age? no: SIGN IN TO CONFIRM YOU'RE NOT A BOT")]
    public void BotCheck_IsRecognised(string stderr)
    {
        Assert.True(YtDlpParsing.IsBotCheckError(stderr));
        Assert.Equal(YtDlpFailureKind.BotCheck, YtDlpParsing.ClassifyFailure(stderr));
    }

    [Theory]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden")]
    [InlineData("ERROR: [youtube] abc: Video unavailable")]
    [InlineData("ERROR: [youtube] abc: Requested format is not available")]
    [InlineData("")]
    public void OtherFailures_AreNotABotCheck(string stderr)
    {
        Assert.False(YtDlpParsing.IsBotCheckError(stderr));
        Assert.Equal(YtDlpFailureKind.Other, YtDlpParsing.ClassifyFailure(stderr));
    }

    [Theory]
    [InlineData("ERROR: Could not copy Chrome cookie database. See  https://github.com/yt-dlp/yt-dlp/issues/7271  for more info", YtDlpFailureKind.CookiesUnreadable)]
    [InlineData("Extracting cookies from edge\nERROR: Failed to decrypt with DPAPI. See  https://github.com/yt-dlp/yt-dlp/issues/10927  for more info", YtDlpFailureKind.CookiesUnreadable)]
    [InlineData("ERROR: could not find firefox cookies database in \"C:\\Users\\me\\AppData\\Roaming\\Mozilla\\Firefox\\Profiles\"", YtDlpFailureKind.CookiesNotFound)]
    [InlineData("ERROR: could not find brave cookies database in \"C:\\Users\\me\\AppData\\Local\\BraveSoftware\\Brave-Browser\\User Data\"", YtDlpFailureKind.CookiesNotFound)]
    [InlineData("ERROR: unsupported browser specified for cookies: \"netscape\". Supported browsers are: brave, chrome", YtDlpFailureKind.CookiesNotFound)]
    [InlineData(RealBotLine, YtDlpFailureKind.Other)] // mentions --cookies, but is the bot check
    public void CookieReadFailures_AreClassified(string stderr, YtDlpFailureKind expected)
        => Assert.Equal(expected, YtDlpParsing.ClassifyCookieError(stderr));

    [Fact]
    public void CookiesArg_GoesBeforeTheUrl_OnlyForAKnownBrowser()
    {
        var args = YtDlpParsing.StreamUrlArgs("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        Assert.Same(args, YtDlpParsing.WithCookiesFromBrowser(args, null));
        Assert.Same(args, YtDlpParsing.WithCookiesFromBrowser(args, "netscape"));

        var with = YtDlpParsing.WithCookiesFromBrowser(args, "Edge");
        Assert.Equal(args.Count + 2, with.Count);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", with[^1]);
        Assert.Equal("--cookies-from-browser", with[^3]);
        Assert.Equal("edge", with[^2]);
        Assert.Same(with, YtDlpParsing.WithCookiesFromBrowser(with, "edge")); // never twice
    }

    [Fact]
    public void Messages_NameTheBrowser_AndSayWhatToDo()
    {
        Assert.Equal(Loc.T("YtDlp.BotCheck"), YtDlpParsing.FailureMessage(YtDlpFailureKind.BotCheck, null));
        Assert.Equal(Loc.T("YtDlp.BotCheckSignedIn", "Firefox"), YtDlpParsing.FailureMessage(YtDlpFailureKind.BotCheck, "firefox"));
        Assert.Equal(Loc.T("YtDlp.CookiesUnreadable", "Chrome"), YtDlpParsing.FailureMessage(YtDlpFailureKind.CookiesUnreadable, "chrome"));
        Assert.Equal(Loc.T("YtDlp.CookiesNotFound", "Brave"), YtDlpParsing.FailureMessage(YtDlpFailureKind.CookiesNotFound, "brave"));
        Assert.Contains("Chrome", YtDlpParsing.FailureMessage(YtDlpFailureKind.CookiesUnreadable, "chrome"));
        Assert.Equal(string.Empty, YtDlpParsing.FailureMessage(YtDlpFailureKind.Other, "chrome"));
    }

    // ── yt-dlp through its process seam ──

    private sealed class Runs
    {
        public readonly List<IReadOnlyList<string>> All = new();
        public string Stderr = string.Empty;
        public int Fetches, Updates;
    }

    private YtDlpTool Tool(Runs runs, Func<string?>? cookies = null)
    {
        var tool = new YtDlpTool(new HttpClient(), _dir, () => string.Empty, cookies);
        Directory.CreateDirectory(tool.ToolsDirectory);
        File.WriteAllText(tool.InstalledPath, "fake");
        tool.JsRuntimes = (true, false);
        tool.LatestVersionFetcher = _ => { runs.Fetches++; return Task.FromResult<string?>("2099.01.01"); };
        tool.Updater = _ => { runs.Updates++; return Task.CompletedTask; };
        tool.Runner = (exe, args, onLine, ct) =>
        {
            if (args is ["--version"]) return Task.FromResult((0, "2026.10.21\n", string.Empty));
            runs.All.Add(args);
            if (runs.Stderr.Length > 0) return Task.FromResult((1, string.Empty, runs.Stderr));
            if (args.Contains("-g"))
                return Task.FromResult((0, "https://rr1---sn-x.googlevideo.com/videoplayback?expire=1795679982&itag=137\n", string.Empty));
            return Task.FromResult((0, "{\"id\":\"dQw4w9WgXcQ\",\"title\":\"Rick Astley - Never Gonna Give You Up\",\"channel\":\"Rick Astley\",\"duration\":213}\n", string.Empty));
        };
        return tool;
    }

    [Fact]
    public async Task BotCheck_ThrowsItsOwnKind_WithoutUpdatingOrRetrying()
    {
        var runs = new Runs { Stderr = RealBotLine + "\n" };
        var tool = Tool(runs);

        var ex = await Assert.ThrowsAsync<YtDlpAccessException>(() => tool.ResolveStreamUrlAsync("FGBhQbmPwH8", 1080, CancellationToken.None));
        Assert.Equal(YtDlpFailureKind.BotCheck, ex.Kind);
        Assert.Equal(Loc.T("YtDlp.BotCheck"), ex.Message);
        Assert.IsType<YtDlpRunException>(ex.InnerException);
        Assert.Single(runs.All);       // YouTube was asked once
        Assert.Equal(0, runs.Updates); // an update does not get past a bot check

        await Assert.ThrowsAsync<YtDlpAccessException>(() => tool.SearchAsync("Rick Astley Never Gonna Give You Up", 8, CancellationToken.None));
        await Assert.ThrowsAsync<YtDlpAccessException>(() => tool.GetInfoAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ", CancellationToken.None));
        Assert.Equal(3, runs.All.Count);
    }

    [Fact]
    public async Task CookiesArg_OnlyWhileTheSettingIsOn_WithTheChosenBrowser()
    {
        var runs = new Runs();
        string? browser = null;
        var tool = Tool(runs, () => browser);

        await tool.SearchAsync("Rick Astley Never Gonna Give You Up", 8, CancellationToken.None);
        await tool.ResolveStreamUrlAsync("dQw4w9WgXcQ", 1080, CancellationToken.None);
        Assert.All(runs.All, a => Assert.DoesNotContain("--cookies-from-browser", a));

        runs.All.Clear();
        browser = "edge";
        await tool.SearchAsync("Rick Astley Never Gonna Give You Up", 8, CancellationToken.None);
        await tool.ResolveStreamUrlAsync("dQw4w9WgXcQ", 1080, CancellationToken.None);
        await tool.GetInfoAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ", CancellationToken.None);
        var target = Path.Combine(_dir, "dl");
        Directory.CreateDirectory(target);
        await Assert.ThrowsAnyAsync<Exception>(() => tool.DownloadVideoAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ", target, null, 1080, null, CancellationToken.None));
        Assert.Equal(4, runs.All.Count);
        foreach (var a in runs.All)
        {
            var i = a.ToList().IndexOf("--cookies-from-browser");
            Assert.True(i >= 0);
            Assert.Equal("edge", a[i + 1]);
            Assert.Equal(a.Count - 3, i); // right before the URL / search
        }
        Assert.StartsWith("ytsearch8:", runs.All[0][^1]);
    }

    [Fact]
    public async Task BotCheck_WithTheSignInOn_SaysToSignIn_InThatBrowser()
    {
        var runs = new Runs { Stderr = RealBotLine };
        var tool = Tool(runs, () => "firefox");
        var ex = await Assert.ThrowsAsync<YtDlpAccessException>(() => tool.SearchAsync("q", 8, CancellationToken.None));
        Assert.Equal(Loc.T("YtDlp.BotCheckSignedIn", "Firefox"), ex.Message);
        Assert.Equal("firefox", ex.CookieBrowser);
    }

    [Fact]
    public async Task CookieReadFailure_IsAReadableMessage()
    {
        var runs = new Runs { Stderr = "[Cookies] Extracting cookies from chrome\nERROR: Could not copy Chrome cookie database. See  https://github.com/yt-dlp/yt-dlp/issues/7271  for more info\n" };
        var tool = Tool(runs, () => "chrome");
        var ex = await Assert.ThrowsAsync<YtDlpAccessException>(() => tool.ResolveStreamUrlAsync("dQw4w9WgXcQ", 1080, CancellationToken.None));
        Assert.Equal(YtDlpFailureKind.CookiesUnreadable, ex.Kind);
        Assert.Equal(Loc.T("YtDlp.CookiesUnreadable", "Chrome"), ex.Message);
        Assert.Single(runs.All);
        Assert.Equal(0, runs.Updates);
    }

    // ── The service: cool-down, saved choices kept ──

    private sealed class FakeSource : IYouTubeVideoSource
    {
        public bool IsAvailable => true;
        public int Searches, Infos;
        public List<string> Resolves { get; } = new();
        public Exception? Error { get; set; }
        private int _n;

        public Task<IReadOnlyList<YouTubeVideoInfo>> SearchAsync(string query, int count, CancellationToken ct)
        {
            Searches++;
            if (Error is { } e) return Task.FromException<IReadOnlyList<YouTubeVideoInfo>>(e);
            return Task.FromResult<IReadOnlyList<YouTubeVideoInfo>>(new[]
            {
                new YouTubeVideoInfo("dQw4w9WgXcQ", "Rick Astley - Never Gonna Give You Up (Official Video)", "Rick Astley", 213, 1_000_000_000, true),
            });
        }

        public Task<YouTubeVideoInfo?> GetInfoAsync(string url, CancellationToken ct)
        {
            Infos++;
            if (Error is { } e) return Task.FromException<YouTubeVideoInfo?>(e);
            return Task.FromResult<YouTubeVideoInfo?>(new YouTubeVideoInfo(YouTubeLinks.TryGetVideoId(url)!, "Pasted", "Channel", 200));
        }

        public Task<string> ResolveStreamUrlAsync(string videoId, int maxHeight, CancellationToken ct)
        {
            Resolves.Add(videoId);
            if (Error is { } e) return Task.FromException<string>(e);
            var n = Interlocked.Increment(ref _n);
            return Task.FromResult($"https://rr1---sn-x.googlevideo.com/videoplayback?expire=1795679982&id={videoId}&n={n}");
        }
    }

    private static Exception BotCheck(string? browser = null) =>
        new YtDlpAccessException(YtDlpFailureKind.BotCheck, browser, new YtDlpRunException(1, RealBotLine, RealBotLine));

    private static readonly DateTime Start = DateTimeOffset.FromUnixTimeSeconds(1_795_670_000).UtcDateTime;

    private (MusicVideoStreamService Service, MusicVideoChoiceStore Store, FakeSource Source) MakeService(Func<DateTime> clock)
    {
        var source = new FakeSource();
        var store = new MusicVideoChoiceStore(Path.Combine(_dir, "music_video_choices.json"));
        return (new MusicVideoStreamService(source, store) { Clock = clock }, store, source);
    }

    private static Track Song(string title, string artist = "Rick Astley") => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = artist,
        Duration = TimeSpan.FromSeconds(213),
        FilePath = @"C:\Music\" + artist + @"\" + title + ".flac",
    };

    [Fact]
    public async Task BotCheck_PausesAutomaticSearches_ForThirtyMinutes_AndMarksNoMatch_Never()
    {
        var now = Start;
        var (service, store, source) = MakeService(() => now);
        var problems = 0;
        service.ProblemChanged += (_, _) => problems++;
        source.Error = BotCheck();
        var first = Song("Never Gonna Give You Up");

        Assert.Null(await service.FindForTrackAsync(first, autoFind: true, CancellationToken.None));
        Assert.Equal(1, source.Searches);
        Assert.Null(store.Get(first.Id)); // not "no match": it searches again later
        Assert.True(service.IsCoolingDown);
        Assert.Equal(YtDlpFailureKind.BotCheck, service.LastProblem!.Kind);
        Assert.Equal(Loc.T("YtDlp.BotCheck"), service.LastProblem.Message);
        Assert.Equal(1, problems);

        // Song after song during the pause: nothing asks YouTube.
        for (var i = 0; i < 5; i++)
        {
            now += TimeSpan.FromMinutes(5);
            Assert.Null(await service.FindForTrackAsync(Song("Song " + i), autoFind: true, CancellationToken.None));
        }
        Assert.Equal(1, source.Searches);

        // After the pause YouTube is asked again; a run that gets through ends the message.
        now = Start + MusicVideoStreamService.BotCheckCooldown + TimeSpan.FromSeconds(1);
        source.Error = null;
        Assert.NotNull(await service.FindForTrackAsync(first, autoFind: true, CancellationToken.None));
        Assert.Equal(2, source.Searches);
        Assert.False(service.IsCoolingDown);
        Assert.Null(service.LastProblem);
    }

    [Fact]
    public async Task BotCheck_KeepsSavedChoices_AndSkipsTheirResolveDuringThePause()
    {
        var now = Start;
        var (service, store, source) = MakeService(() => now);
        var mine = Song("Together Forever");
        var auto = Song("Never Gonna Give You Up");
        service.SetUserVideo(mine, new YouTubeVideoInfo("dQw4w9WgXcQ", "Official Video", "Rick Astley", 213));
        Assert.NotNull(await service.FindForTrackAsync(auto, autoFind: true, CancellationToken.None)); // remembered auto match
        var autoChoice = store.Get(auto.Id);
        Assert.NotNull(autoChoice);

        source.Error = BotCheck();
        now += TimeSpan.FromHours(6); // the cached URLs are stale: a resolve is needed
        var resolves = source.Resolves.Count;
        Assert.Null(await service.FindForTrackAsync(auto, autoFind: true, CancellationToken.None));
        Assert.Equal(resolves + 1, source.Resolves.Count);
        Assert.Equal(autoChoice!.VideoId, store.Get(auto.Id)?.VideoId); // a bot check is not "video gone"

        Assert.Null(await service.FindForTrackAsync(mine, autoFind: false, CancellationToken.None));
        Assert.Null(await service.RefreshAsync(new MusicVideoStream("dQw4w9WgXcQ", "https://x.googlevideo.com/a", now, true), CancellationToken.None));
        Assert.Equal(resolves + 1, source.Resolves.Count); // nothing more asked YouTube
        Assert.Equal(MusicVideoChoiceSource.User, store.Get(mine.Id)?.Source);
        Assert.Equal("dQw4w9WgXcQ", store.Get(mine.Id)?.VideoId);
    }

    [Fact]
    public async Task CookieFailure_IsShown_ButDoesNotPause()
    {
        var (service, _, source) = MakeService(() => Start);
        source.Error = new YtDlpAccessException(YtDlpFailureKind.CookiesNotFound, "brave");
        Assert.Null(await service.FindForTrackAsync(Song("Never Gonna Give You Up"), autoFind: true, CancellationToken.None));
        Assert.False(service.IsCoolingDown); // nothing reached YouTube
        Assert.Equal(Loc.T("YtDlp.CookiesNotFound", "Brave"), service.LastProblem!.Message);
    }

    // ── The player: recovery does not hammer YouTube ──

    private static PlayerViewModel Player(MusicVideoStreamService service, List<TimeSpan> delays)
    {
        var vm = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            MusicVideosEnabled = true,
            MusicVideoAutoFindEnabled = false,
            MusicVideoStreams = service,
        };
        vm.StreamClock = () => Start;
        vm.StreamRetryDelay = (d, _) => { delays.Add(d); return Task.CompletedTask; };
        vm.IsNetworkUp = () => true;
        return vm;
    }

    private static async Task Play(PlayerViewModel vm, MusicVideoStreamService service, Track track)
    {
        service.SetUserVideo(track, new YouTubeVideoInfo("dQw4w9WgXcQ", "Official Video", "Rick Astley", 213));
        vm.CurrentTrack = track;
        await vm.MusicVideoStreamTask;
        Assert.True(vm.IsStreamingMusicVideo);
    }

    [AvaloniaFact]
    public async Task Recovery_StopsAtTheFirstBotCheck_InsteadOfUsingEveryRetry()
    {
        var (service, _, source) = MakeService(() => Start);
        var delays = new List<TimeSpan>();
        var vm = Player(service, delays);
        await Play(vm, service, Song("Never Gonna Give You Up"));
        source.Error = BotCheck();

        vm.MusicVideoStreamFailedCommand.Execute(vm.CurrentMusicVideoPath);
        await vm.MusicVideoStreamTask;

        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, delays); // one try, not 2 s / 5 s / 15 s
        Assert.Equal(2, source.Resolves.Count);                    // the first play + that one try
        Assert.False(vm.IsStreamingMusicVideo);
        Assert.True(service.IsCoolingDown);
    }

    [AvaloniaFact]
    public async Task Recovery_DuringThePause_DoesNotAskYouTubeAtAll()
    {
        var (service, _, source) = MakeService(() => Start);
        var delays = new List<TimeSpan>();
        var vm = Player(service, delays);
        await Play(vm, service, Song("Never Gonna Give You Up"));
        service.NoteFailure(BotCheck()); // e.g. the dialog's search was refused meanwhile

        vm.MusicVideoStreamFailedCommand.Execute(vm.CurrentMusicVideoPath);
        await vm.MusicVideoStreamTask;

        Assert.Empty(delays);
        Assert.Single(source.Resolves);
    }

    // ── Where the user looks: the dialog and Settings ──

    [AvaloniaFact]
    public async Task Dialog_ShowsThePlainMessage_ForSearchAndLink()
    {
        var (service, _, source) = MakeService(() => Start);
        source.Error = BotCheck();
        var picker = new MusicVideoPickerViewModel(Song("Never Gonna Give You Up"), service);

        await picker.SearchCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("YtDlp.BotCheck"), picker.SearchStatus); // no "Search failed:" in front
        Assert.Equal(1, source.Searches); // the user asked: it runs even while paused...
        await picker.SearchCommand.ExecuteAsync(null);
        Assert.Equal(2, source.Searches);

        picker.LinkText = "https://youtu.be/dQw4w9WgXcQ";
        await picker.LinkLookup;
        Assert.Equal(Loc.T("YtDlp.BotCheck"), picker.LinkStatus);
        Assert.Equal(1, source.Infos);
        Assert.True(service.IsCoolingDown);

        // ...and one that gets through ends the pause and the message.
        source.Error = null;
        await picker.SearchCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, picker.SearchStatus);
        Assert.False(service.IsCoolingDown);
        Assert.Null(service.LastProblem);
    }

    [AvaloniaFact]
    public async Task Settings_SignIn_DefaultsOff_PassesTheChosenBrowser_AndShowsTheNote()
    {
        var (service, _, _) = MakeService(() => Start);
        var settings = new SettingsViewModel(new TestPersistenceService(), new FakeLibraryService(), new FakeHistoryLog());
        await settings.LoadAsync();
        settings.AttachMusicVideoStreams(service);

        Assert.False(new AppSettings().MusicVideoUseBrowserCookies);
        Assert.False(settings.MusicVideoUseBrowserCookies);
        Assert.Null(settings.MusicVideoCookiesFromBrowser);
        Assert.Equal("Firefox", settings.MusicVideoCookieBrowserName);
        Assert.Contains("Chrome", settings.MusicVideoCookieBrowserNames);
        Assert.Contains("Edge", settings.MusicVideoCookieBrowserNames);
        Assert.False(settings.HasMusicVideoProblem);

        service.NoteFailure(BotCheck());
        Assert.True(settings.HasMusicVideoProblem);
        Assert.Equal(Loc.T("YtDlp.BotCheck"), settings.MusicVideoProblemText);

        // Turning the sign-in on: the pause and the old message end, the browser is passed.
        settings.MusicVideoCookieBrowserName = "Edge";
        Assert.True(service.IsCoolingDown); // picking a browser while off changes nothing yet
        settings.MusicVideoUseBrowserCookies = true;
        Assert.Equal("edge", settings.MusicVideoCookiesFromBrowser);
        Assert.False(service.IsCoolingDown);
        Assert.False(settings.HasMusicVideoProblem);
        await settings.SaveAsync();
        Assert.Equal("edge", settings.GetSettings().MusicVideoCookiesBrowser);
        Assert.True(settings.GetSettings().MusicVideoUseBrowserCookies);
    }

    // ── Issue 2: slow start / slow seek is not a stall ──

    private static void Check(StreamSyncGate gate, ref long now, long untilMs, long stepMs, long songMs, bool expectStalled)
    {
        for (; now <= untilMs; now += stepMs)
            Assert.Equal(expectStalled, gate.IsStalled(songMs + now, 200_000));
    }

    [Fact]
    public void SlowStart_FirstFrameAfter20s_IsNotAStall()
    {
        long now = 0;
        var gate = new StreamSyncGate(() => now);
        gate.Want(0, force: true, playing: true, rate: 1);
        gate.SeekIssued(250);              // the forced first sync
        now = StreamSyncGate.SeekCapMs + 50;
        gate.OnCapElapsed();               // no frame by the cap: the seek is over, still no frame
        Check(gate, ref now, 20_000, 100, 0, expectStalled: false);
        Assert.True(gate.IsAwaitingFrame);

        gate.NoteFrame();                  // first frame at ~20 s
        Assert.False(gate.IsAwaitingFrame);
        Assert.False(gate.IsStalled(20_000, 200_000));
    }

    [Fact]
    public void AfterTheFirstFrame_AStall_IsStillCaughtAt12s()
    {
        long now = 0;
        var gate = new StreamSyncGate(() => now);
        now = 3_000;
        gate.NoteFrame();
        now += StreamSyncGate.StallMs - 1;
        Assert.False(gate.IsStalled(30_000, 200_000));
        now += 2;
        Assert.True(gate.IsStalled(30_000, 200_000));
    }

    [Fact]
    public void StartUp_HasItsOwnCap_AndBufferingProgressKeepsItAlive()
    {
        long now = 0;
        var gate = new StreamSyncGate(() => now);
        now = StreamSyncGate.StartupCapMs - 1;
        Assert.False(gate.IsStalled(0, 200_000));
        now = StreamSyncGate.StartupCapMs + 1;
        Assert.True(gate.IsStalled(0, 200_000)); // nothing at all for 30 s: broken

        // VLC filling its cache slowly: never a stall while the percentage moves.
        now = 0;
        var slow = new StreamSyncGate(() => now);
        for (var pct = 1f; pct <= 60f; pct += 1f)
        {
            now += 1_000;
            slow.NoteBuffering(pct);
            Assert.False(slow.IsStalled(0, 200_000));
        }
        // The same value over and over is no progress.
        var stuckAt = now;
        while (now < stuckAt + StreamSyncGate.StartupCapMs + 1_000)
        {
            now += 1_000;
            slow.NoteBuffering(60f);
        }
        Assert.True(slow.IsStalled(0, 200_000));
    }

    [Fact]
    public void BufferingAfterTheFirstFrame_KeepsItAlive_Too()
    {
        long now = 0;
        var gate = new StreamSyncGate(() => now);
        gate.NoteFrame();
        for (var i = 0; i < 30; i++)
        {
            now += 1_000;
            gate.NoteBuffering(i * 3);
        }
        Assert.False(gate.IsStalled(30_000, 200_000));
    }

    [Fact]
    public void SlowSeek_DoesNotTripTheStallDetector_ButAFrameAfterItReArmsIt()
    {
        long now = 0;
        var gate = new StreamSyncGate(() => now);
        gate.NoteFrame();                       // playing
        now = 10_000;
        gate.Want(90_000, force: false, playing: true, rate: 1); // the song was sought
        Assert.True(gate.TryTake(out var target, out var jumped));
        var (_, to) = gate.Plan(target, jumped, ended: false, videoMs: 10_000, lengthMs: 200_000, playing: true, rate: 1);
        gate.SeekIssued(to);
        // A stale frame of the old position while the seek is in flight does not arm it.
        now += 200;
        gate.NoteFrame();
        Assert.True(gate.IsAwaitingFrame);
        now += StreamSyncGate.SeekCapMs;
        gate.OnCapElapsed();                    // the 1.5 s cap ends the seek; no frame yet
        Assert.False(gate.IsSeeking);
        var seekAt = now;
        while (now < seekAt + 20_000)           // 20 s re-buffering after the seek
        {
            now += 250;
            Assert.False(gate.IsStalled(90_000 + (now - 10_000), 200_000));
        }
        gate.NoteFrame();                       // the seek's first frame
        Assert.False(gate.IsAwaitingFrame);
        now += StreamSyncGate.StallMs + 1;
        Assert.True(gate.IsStalled(120_000, 200_000)); // a stall after that is caught again
    }

    [Fact]
    public void ASeekThatLands_ArmsTheStallDetector()
    {
        long now = 0;
        var gate = new StreamSyncGate(() => now);
        gate.SeekIssued(5_000);
        now = 400;
        gate.OnFrame(5_100);
        Assert.False(gate.IsAwaitingFrame);
        Assert.False(gate.IsSeeking);
    }
}
