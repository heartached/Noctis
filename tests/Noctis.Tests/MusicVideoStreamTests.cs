using Avalonia.Headless.XUnit;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services.YouTube;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Music videos from YouTube behind the lyrics (Discord #feedback 11-25, "Automatic YouTube
/// Music Video Streaming"): link parsing, yt-dlp output parsing, candidate scoring against
/// realistic decoys, the per-song choice (the user's wins, "no video"), the setting gate,
/// cancellation on a track change and the stream URL refresh before it expires. All offline:
/// yt-dlp is a fake source or a fake process runner.
/// </summary>
public class MusicVideoStreamTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-mvstream-" + Guid.NewGuid().ToString("N")[..8]);

    public MusicVideoStreamTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── Links ──

    [Theory]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abcDEF123&t=42", "dQw4w9WgXcQ")]
    [InlineData("youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=1m2s", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLx0sYbCqOb8TBPRdmBHs5Iftvv9TPboYG&index=3", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?feature=share&v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&list=RDAMVMdQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/shorts/dQw4w9WgXcQ?feature=share", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ?start=10", "dQw4w9WgXcQ")]
    [InlineData("  https://www.youtube.com/watch?v=dQw4w9WgXcQ  ", "dQw4w9WgXcQ")]
    public void TryGetVideoId_ReadsEveryLinkShape(string link, string expected)
    {
        Assert.Equal(expected, YouTubeLinks.TryGetVideoId(link));
    }

    [Theory]
    [InlineData("")]
    [InlineData("never gonna give you up")]
    [InlineData("https://www.youtube.com/playlist?list=PLx0sYbCqOb8TBPRdmBHs5Iftvv9TPboYG")]
    [InlineData("https://www.youtube.com/@RickAstleyYT")]
    [InlineData("https://vimeo.com/123456789")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://evil.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("ftp://youtu.be/dQw4w9WgXcQ")]
    public void TryGetVideoId_RejectsWhatIsNotOneVideo(string link)
    {
        Assert.Null(YouTubeLinks.TryGetVideoId(link));
    }

    [Fact]
    public void WatchUrl_DropsPlaylistAndTime()
    {
        var id = YouTubeLinks.TryGetVideoId("https://music.youtube.com/watch?v=dQw4w9WgXcQ&list=RD&t=30")!;
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", YouTubeLinks.WatchUrl(id));
    }

    // ── yt-dlp output ──

    /// <summary>One --flat-playlist --dump-json line, shaped like yt-dlp 2026.10.21's (measured 11-25).</summary>
    private static string Line(string id, string title, string channel, double duration, long views, bool verified = false, string? live = null) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["_type"] = "url", ["ie_key"] = "Youtube", ["id"] = id,
            ["url"] = "https://www.youtube.com/watch?v=" + id, ["title"] = title,
            ["description"] = "…", ["duration"] = duration, ["channel_id"] = "UC" + id, ["channel"] = channel,
            ["uploader"] = channel, ["view_count"] = views, ["live_status"] = live,
            ["channel_is_verified"] = verified ? true : null, ["duration_string"] = "3:34",
        });

    /// <summary>A realistic "ytsearch8:" for Rick Astley – Never Gonna Give You Up (3:33),
    /// the official video NOT first, with the decoys a search really returns.</summary>
    private static string RickSearch() => string.Join("\n", new[]
    {
        Line("LM8dsLtUs2A", "Rick Astley - Never Gonna Give You Up (Official Music Video)", "Rick Astley", 213, 507),                 // re-upload, 507 views
        Line("lyricVid001", "Rick Astley - Never Gonna Give You Up (Lyrics)", "7clouds", 213, 40_000_000),
        Line("dQw4w9WgXcQ", "Rick Astley - Never Gonna Give You Up (Official Video) (4K Remaster)", "Rick Astley", 214, 1_840_998_410, verified: true),
        Line("liveVid0001", "Rick Astley - Never Gonna Give You Up (Live at Glastonbury 2023)", "Rick Astley", 260, 9_000_000, verified: true),
        Line("coverVid001", "Never Gonna Give You Up - Rick Astley (Cover by Some Band)", "Some Band", 220, 300_000),
        Line("slowedVid01", "rick astley - never gonna give you up (slowed + reverb)", "slowed vibes", 280, 2_000_000),
        Line("topicVid001", "Never Gonna Give You Up", "Rick Astley - Topic", 213, 90_000_000),
        Line("wrongArtist", "Never Gonna Give You Up (Official Video)", "Somebody Else", 213, 5_000_000, verified: true),
        Line("liveNow0001", "Rick Astley 24/7 radio", "Rick Astley", 0, 100, live: "is_live"),
        "WARNING: [youtube] some warning",
    }) + "\n";

    [Fact]
    public void ParseVideoInfos_ReadsSearchLines_SkipsLiveStreamsAndNoise()
    {
        var found = YtDlpParsing.ParseVideoInfos(RickSearch());

        Assert.Equal(8, found.Count);
        Assert.DoesNotContain(found, v => v.Id == "liveNow0001");
        var official = found.Single(v => v.Id == "dQw4w9WgXcQ");
        Assert.Equal("Rick Astley", official.Channel);
        Assert.Equal(214, official.DurationSeconds);
        Assert.Equal(1_840_998_410, official.ViewCount);
        Assert.True(official.ChannelVerified);
        Assert.Equal("3:34", official.DurationText);
        Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/mqdefault.jpg", official.ThumbnailUrl);
    }

    [Fact]
    public void StreamUrl_AndItsExpiry_AreRead()
    {
        const string url = "https://rr2---sn-abc.googlevideo.com/videoplayback?expire=1795679982&ei=x&ip=1.2.3.4&itag=137";
        Assert.Equal(url, YtDlpParsing.ParseStreamUrl("\n" + url + "\n"));
        Assert.Null(YtDlpParsing.ParseStreamUrl("ERROR: nope"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795679982).UtcDateTime, YtDlpParsing.StreamUrlExpiry(url));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795679982).UtcDateTime,
            YtDlpParsing.StreamUrlExpiry("https://manifest.googlevideo.com/api/manifest/expire/1795679982/ei/x"));
        Assert.Null(YtDlpParsing.StreamUrlExpiry("https://example.com/video.mp4"));
    }

    [Fact]
    public void StreamUrlArgs_AskForVideoOnly_H264First_Capped()
    {
        var args = YtDlpParsing.StreamUrlArgs("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        Assert.Contains("-g", args);
        Assert.Contains("--no-playlist", args);
        var format = args[args.ToList().IndexOf("-f") + 1];
        Assert.StartsWith("bestvideo[height<=1080][vcodec^=avc1]/", format);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", args[^1]);

        var search = YtDlpParsing.SearchArgs("Rick Astley Never Gonna Give You Up official music video", 8);
        Assert.Contains("--flat-playlist", search);
        Assert.Contains("--dump-json", search);
        Assert.Equal("ytsearch8:Rick Astley Never Gonna Give You Up official music video", search[^1]);
    }

    // ── Scoring ──

    [Fact]
    public void PickBest_FindsTheOfficialVideo_AmongDecoys()
    {
        var results = YtDlpParsing.ParseVideoInfos(RickSearch());
        var best = MusicVideoMatcher.PickBest(results, "Rick Astley", "Never Gonna Give You Up", 213);
        Assert.Equal("dQw4w9WgXcQ", best?.Id);

        var ranked = MusicVideoMatcher.Rank(results, "Rick Astley", "Never Gonna Give You Up", 213);
        Assert.Equal("dQw4w9WgXcQ", ranked[0].Id);
        Assert.Equal("LM8dsLtUs2A", ranked[1].Id); // same title, but a 507-view re-upload
    }

    [Theory]
    [InlineData("lyricVid001")]
    [InlineData("liveVid0001")]
    [InlineData("coverVid001")]
    [InlineData("slowedVid01")]
    [InlineData("topicVid001")]
    [InlineData("wrongArtist")]
    public void Decoys_AloneAreBelowTheThreshold(string id)
    {
        var decoy = YtDlpParsing.ParseVideoInfos(RickSearch()).Single(v => v.Id == id);
        Assert.Null(MusicVideoMatcher.PickBest(new[] { decoy }, "Rick Astley", "Never Gonna Give You Up", 213));
    }

    [Fact]
    public void ADecoyWord_InTheSongsOwnTitle_IsNotADecoy()
    {
        var results = YtDlpParsing.ParseVideoInfos(RickSearch());
        var live = results.Single(v => v.Id == "liveVid0001") with { Title = "Rick Astley - Never Gonna Give You Up (Live at Glastonbury 2023)" };
        var best = MusicVideoMatcher.PickBest(new[] { live }, "Rick Astley", "Never Gonna Give You Up (Live at Glastonbury 2023)", 258);
        Assert.Equal("liveVid0001", best?.Id);
    }

    [Fact]
    public void Matching_IgnoresAccentsFeatAndRemasterNotes()
    {
        var video = new YouTubeVideoInfo("abcdefghijk", "Beyoncé - Déjà Vu ft. Jay-Z (Official Video)", "BeyonceVEVO", 241, 300_000_000, true);
        Assert.Equal("abcdefghijk",
            MusicVideoMatcher.PickBest(new[] { video }, "Beyonce feat. Jay-Z", "Deja Vu (2006 Remaster)", 240)?.Id);
        Assert.Equal("Beyonce Deja Vu official music video", MusicVideoMatcher.SearchQuery("Beyonce feat. Jay-Z", "Deja Vu (2006 Remaster)"));
        Assert.Equal(MusicVideoMatcher.MatchKey("Beyoncé", "Déjà Vu"), MusicVideoMatcher.MatchKey("beyonce", "Deja Vu (Remastered)"));
    }

    [Fact]
    public void ALengthFarOff_IsNotUsed()
    {
        var hour = new YouTubeVideoInfo("abcdefghijk", "Rick Astley - Never Gonna Give You Up (Official Video)", "Rick Astley", 3600, 5_000_000, true);
        var tenMin = new YouTubeVideoInfo("abcdefghijl", "Rick Astley - Never Gonna Give You Up (Official Video)", "Rick Astley", 600, 5_000_000, false);
        Assert.Null(MusicVideoMatcher.PickBest(new[] { tenMin }, "Rick Astley", "Never Gonna Give You Up", 213));
        Assert.True(MusicVideoMatcher.Score(hour, "Rick Astley", "Never Gonna Give You Up", 213)
                    < MusicVideoMatcher.Score(hour, "Rick Astley", "Never Gonna Give You Up", 3600));
    }

    // ── Service ──

    private sealed class FakeSource : IYouTubeVideoSource
    {
        public bool IsAvailable { get; set; } = true;
        public List<string> Searches { get; } = new();
        public List<string> Resolves { get; } = new();
        public string SearchOutput { get; set; } = RickSearch();
        public Exception? SearchError { get; set; }
        public Exception? ResolveError { get; set; }
        public TaskCompletionSource? SearchGate { get; set; }
        public int UrlCounter;
        public long ExpireUnix { get; set; } = 1_795_679_982;

        public async Task<IReadOnlyList<YouTubeVideoInfo>> SearchAsync(string query, int count, CancellationToken ct)
        {
            Searches.Add(query);
            if (SearchGate is { } gate) await gate.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (SearchError is { } e) throw e;
            return YtDlpParsing.ParseVideoInfos(SearchOutput);
        }

        public Task<YouTubeVideoInfo?> GetInfoAsync(string url, CancellationToken ct)
        {
            var id = YouTubeLinks.TryGetVideoId(url)!;
            return Task.FromResult<YouTubeVideoInfo?>(new YouTubeVideoInfo(id, "Pasted video " + id, "Some Channel", 200));
        }

        public Task<string> ResolveStreamUrlAsync(string videoId, int maxHeight, CancellationToken ct)
        {
            Resolves.Add(videoId);
            if (ResolveError is { } e) return Task.FromException<string>(e);
            var n = Interlocked.Increment(ref UrlCounter);
            return Task.FromResult($"https://rr1---sn-x.googlevideo.com/videoplayback?expire={ExpireUnix}&id={videoId}&n={n}");
        }
    }

    private static readonly DateTime Now = DateTimeOffset.FromUnixTimeSeconds(1_795_670_000).UtcDateTime; // ~2.8 h before the fake URLs expire

    private (MusicVideoStreamService Service, MusicVideoChoiceStore Store, FakeSource Source) MakeService(Func<DateTime>? clock = null)
    {
        var source = new FakeSource();
        var store = new MusicVideoChoiceStore(Path.Combine(_dir, "music_video_choices.json"));
        var service = new MusicVideoStreamService(source, store) { Clock = clock ?? (() => Now) };
        return (service, store, source);
    }

    private static Track Rick() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Never Gonna Give You Up",
        Artist = "Rick Astley",
        Duration = TimeSpan.FromSeconds(213),
        FilePath = @"C:\Music\Rick Astley\Never Gonna Give You Up.flac",
    };

    [Fact]
    public async Task AutoOff_NoChoice_DoesNotSearch()
    {
        var (service, _, source) = MakeService();
        Assert.Null(await service.FindForTrackAsync(Rick(), autoFind: false, CancellationToken.None));
        Assert.Empty(source.Searches);
        Assert.Empty(source.Resolves);
    }

    [Fact]
    public async Task AutoOn_SearchesOnce_RemembersTheMatch_AndReusesTheUrl()
    {
        var (service, store, source) = MakeService();
        var track = Rick();

        var first = await service.FindForTrackAsync(track, autoFind: true, CancellationToken.None);
        Assert.Equal("dQw4w9WgXcQ", first?.VideoId);
        Assert.False(first!.IsUserChoice);
        Assert.Equal(new[] { "Rick Astley Never Gonna Give You Up official music video" }, source.Searches);
        var saved = store.Get(track.Id)!;
        Assert.Equal(MusicVideoChoiceSource.Auto, saved.Source);
        Assert.Equal("dQw4w9WgXcQ", saved.VideoId);
        Assert.Equal("rick astley|never gonna give you up", saved.MatchKey);

        // Next play (and after a restart: a new service over the same file): no search, the
        // URL from memory while it is fresh.
        var again = await service.FindForTrackAsync(track, autoFind: true, CancellationToken.None);
        Assert.Equal(first.Url, again?.Url);
        Assert.Single(source.Searches);
        Assert.Single(source.Resolves);

        var restarted = new MusicVideoStreamService(source, new MusicVideoChoiceStore(Path.Combine(_dir, "music_video_choices.json"))) { Clock = () => Now };
        Assert.Equal("dQw4w9WgXcQ", (await restarted.FindForTrackAsync(track, true, CancellationToken.None))?.VideoId);
        Assert.Single(source.Searches);
    }

    [Fact]
    public async Task UserChoice_WinsOverTheAutomaticMatch_EvenWithAutoOff()
    {
        var (service, _, source) = MakeService();
        var track = Rick();
        await service.FindForTrackAsync(track, autoFind: true, CancellationToken.None); // auto picks dQw4w9WgXcQ

        service.SetUserVideo(track, new YouTubeVideoInfo("LM8dsLtUs2A", "Their pick", "Rick Astley", 213));
        var mine = await service.FindForTrackAsync(track, autoFind: true, CancellationToken.None);
        Assert.Equal("LM8dsLtUs2A", mine?.VideoId);
        Assert.True(mine!.IsUserChoice);

        var autoOff = await service.FindForTrackAsync(track, autoFind: false, CancellationToken.None);
        Assert.Equal("LM8dsLtUs2A", autoOff?.VideoId);
        Assert.Single(source.Searches);
    }

    [Fact]
    public async Task NoVideoChoice_ShowsNothing_AndNeverSearches()
    {
        var (service, store, source) = MakeService();
        var track = Rick();
        service.SetNoVideo(track);

        Assert.Null(await service.FindForTrackAsync(track, autoFind: true, CancellationToken.None));
        Assert.Empty(source.Searches);
        Assert.Equal(MusicVideoChoiceKind.NoVideo, store.Get(track.Id)!.Kind);

        service.ClearChoice(track); // Back to automatic
        Assert.Equal("dQw4w9WgXcQ", (await service.FindForTrackAsync(track, true, CancellationToken.None))?.VideoId);
    }

    [Fact]
    public async Task NoGoodMatch_IsRemembered_ForTwoWeeks()
    {
        var now = Now;
        var (service, _, source) = MakeService(() => now);
        source.SearchOutput = Line("lyricVid001", "Rick Astley - Never Gonna Give You Up (Lyrics)", "7clouds", 213, 40_000_000) + "\n";
        var track = Rick();

        Assert.Null(await service.FindForTrackAsync(track, true, CancellationToken.None));
        Assert.Null(await service.FindForTrackAsync(track, true, CancellationToken.None));
        Assert.Single(source.Searches);
        Assert.Empty(source.Resolves);

        now += MusicVideoStreamService.NoMatchRetry;
        await service.FindForTrackAsync(track, true, CancellationToken.None);
        Assert.Equal(2, source.Searches.Count);
    }

    [Fact]
    public async Task Offline_OrNoYtDlp_IsNotRemembered_AndDoesNotThrow()
    {
        var (service, store, source) = MakeService();
        var track = Rick();
        source.SearchError = new HttpRequestException("No such host is known.");
        Assert.Null(await service.FindForTrackAsync(track, true, CancellationToken.None));
        Assert.Null(store.Get(track.Id));

        source.SearchError = null;
        source.IsAvailable = false;
        Assert.Null(await service.FindForTrackAsync(track, true, CancellationToken.None));
        Assert.Single(source.Searches); // a missing yt-dlp is not even asked

        source.IsAvailable = true;
        Assert.Equal("dQw4w9WgXcQ", (await service.FindForTrackAsync(track, true, CancellationToken.None))?.VideoId);
    }

    [Fact]
    public async Task ARememberedMatchThatIsGone_IsSearchedAgain()
    {
        var (service, store, source) = MakeService();
        var track = Rick();
        await service.FindForTrackAsync(track, true, CancellationToken.None);

        source.ResolveError = new InvalidOperationException("blocked",
            new YtDlpRunException(1, "ERROR: [youtube] dQw4w9WgXcQ: Video unavailable. This video has been removed by the uploader", "Video unavailable"));
        var fresh = new MusicVideoStreamService(source, store) { Clock = () => Now }; // no URL in memory
        Assert.Null(await fresh.FindForTrackAsync(track, true, CancellationToken.None));
        Assert.Null(store.Get(track.Id)); // dropped...
        Assert.Equal(2, source.Searches.Count); // ...and searched again at once (the fake still fails it)

        source.ResolveError = null;
        Assert.NotNull(await fresh.FindForTrackAsync(track, true, CancellationToken.None));
        Assert.Equal(3, source.Searches.Count);
    }

    [Fact]
    public async Task Refresh_ResolvesANewUrl_AndExpiryIsCappedAndRead()
    {
        var (service, _, source) = MakeService();
        var stream = (await service.FindForTrackAsync(Rick(), true, CancellationToken.None))!;
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(source.ExpireUnix).UtcDateTime, stream.ExpiresUtc); // within 5 h of Now
        Assert.False(stream.NeedsRefresh(Now));
        Assert.True(stream.NeedsRefresh(stream.ExpiresUtc - TimeSpan.FromMinutes(4)));

        var refreshed = await service.RefreshAsync(stream, CancellationToken.None);
        Assert.NotEqual(stream.Url, refreshed!.Url);
        Assert.Equal(2, source.Resolves.Count);

        // A URL valid for days is still only kept 5 h.
        source.ExpireUnix = new DateTimeOffset(Now.AddDays(3)).ToUnixTimeSeconds();
        var capped = await service.RefreshAsync(stream, CancellationToken.None);
        Assert.Equal(Now + MusicVideoStreamService.MaxUrlLifetime, capped!.ExpiresUtc);
    }

    [Fact]
    public async Task ChoiceStore_KeepsAPortableShape()
    {
        var (service, _, _) = MakeService();
        var track = Rick();
        service.SetUserVideo(track, new YouTubeVideoInfo("dQw4w9WgXcQ", "Rick Astley - Never Gonna Give You Up (Official Video)", "Rick Astley", 214));
        var json = await File.ReadAllTextAsync(Path.Combine(_dir, "music_video_choices.json"));
        Assert.Contains("\"MatchKey\": \"rick astley|never gonna give you up\"", json);
        Assert.Contains("\"VideoId\": \"dQw4w9WgXcQ\"", json);
        Assert.Contains("\"Source\": \"User\"", json);
        Assert.Contains("\"DurationSeconds\": 213", json);
    }

    // ── yt-dlp through its process seam ──

    [Fact]
    public async Task YtDlpTool_SearchAndStream_RunTheRightArgs_AndParse()
    {
        var tool = new YtDlpTool(new HttpClient(), _dir, () => string.Empty);
        Directory.CreateDirectory(tool.ToolsDirectory);
        File.WriteAllText(tool.InstalledPath, "fake");
        tool.JsRuntimes = (true, false);
        var runs = new List<IReadOnlyList<string>>();
        tool.Runner = (exe, args, onLine, ct) =>
        {
            if (args is ["--version"]) return Task.FromResult((0, "2026.10.21\n", string.Empty));
            runs.Add(args);
            if (args.Contains("-g"))
                return Task.FromResult((0, "https://rr1---sn-x.googlevideo.com/videoplayback?expire=1795679982&itag=137\n", string.Empty));
            return Task.FromResult((0, RickSearch(), string.Empty));
        };

        var found = await tool.SearchAsync("Rick Astley Never Gonna Give You Up official music video", 8, CancellationToken.None);
        Assert.Equal(8, found.Count);
        Assert.StartsWith("ytsearch8:", runs[0][^1]);

        var url = await tool.ResolveStreamUrlAsync("dQw4w9WgXcQ", 1080, CancellationToken.None);
        Assert.Contains("googlevideo.com", url);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", runs[1][^1]);
    }

    // ── The player ──

    private static PlayerViewModel Player(MusicVideoStreamService service, bool autoFind = true) =>
        new(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            MusicVideosEnabled = true,
            MusicVideoAutoFindEnabled = autoFind,
            MusicVideoStreams = service,
        };

    [AvaloniaFact]
    public async Task Player_StreamsTheMatch_BehindTheLyrics()
    {
        var (service, _, _) = MakeService();
        var vm = Player(service);
        vm.CurrentTrack = Rick();
        await vm.MusicVideoStreamTask;

        Assert.True(vm.IsStreamingMusicVideo);
        Assert.Contains("id=dQw4w9WgXcQ", vm.CurrentMusicVideoPath);
        Assert.True(vm.ShowsMusicVideoBackdrop);   // behind the lyrics, not the cover slot
        Assert.False(vm.ShowsMusicVideoInCover);
        Assert.True(VideoBackdrop.IsRemoteSource(vm.CurrentMusicVideoPath));
    }

    [AvaloniaFact]
    public async Task Player_SettingOff_NeverSearches()
    {
        var (service, _, source) = MakeService();
        var vm = Player(service, autoFind: false);
        vm.CurrentTrack = Rick();
        await vm.MusicVideoStreamTask;

        Assert.Empty(source.Searches);
        Assert.False(vm.IsStreamingMusicVideo);
        Assert.Null(vm.CurrentMusicVideoPath);
    }

    [AvaloniaFact]
    public async Task Player_ALocalClipWins_NoSearch()
    {
        var (service, _, source) = MakeService();
        var song = Path.Combine(_dir, "Never Gonna Give You Up.flac");
        File.WriteAllBytes(song, new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_dir, "Never Gonna Give You Up.mp4"), new byte[] { 1 });
        var vm = Player(service);
        var track = Rick();
        track.FilePath = song;
        vm.CurrentTrack = track;
        await vm.MusicVideoStreamTask;

        Assert.Empty(source.Searches);
        Assert.False(vm.IsStreamingMusicVideo);
        Assert.EndsWith(".mp4", vm.CurrentMusicVideoPath);
    }

    [AvaloniaFact]
    public async Task Player_TrackChange_CancelsTheSearch_AndDropsItsResult()
    {
        var (service, _, source) = MakeService();
        var gate = new TaskCompletionSource();
        source.SearchGate = gate;
        var vm = Player(service);
        vm.CurrentTrack = Rick();
        var first = vm.MusicVideoStreamTask;

        var next = new Track { Id = Guid.NewGuid(), Title = "Other Song", Artist = "Nobody", Duration = TimeSpan.FromSeconds(100), FilePath = @"C:\Music\x.flac" };
        source.SearchOutput = string.Empty; // the next song finds nothing
        vm.CurrentTrack = next;
        gate.SetResult();
        await first;
        await vm.MusicVideoStreamTask;

        Assert.Equal(2, source.Searches.Count);
        Assert.Empty(source.Resolves); // the first song's search was cancelled before resolving
        Assert.False(vm.IsStreamingMusicVideo);
        Assert.Null(vm.CurrentMusicVideoPath);
    }

    [AvaloniaFact]
    public async Task Player_RefreshesTheStreamUrl_BeforeItExpires()
    {
        var (service, _, source) = MakeService();
        var vm = Player(service);
        var now = Now;
        vm.StreamClock = () => now;
        vm.CurrentTrack = Rick();
        await vm.MusicVideoStreamTask;
        var firstUrl = vm.CurrentMusicVideoPath;

        vm.Position = TimeSpan.FromSeconds(10);
        await vm.MusicVideoStreamTask;
        Assert.Equal(firstUrl, vm.CurrentMusicVideoPath); // still fresh

        now = DateTimeOffset.FromUnixTimeSeconds(source.ExpireUnix).UtcDateTime - TimeSpan.FromMinutes(2);
        vm.Position = TimeSpan.FromSeconds(11);
        await vm.MusicVideoStreamTask;

        Assert.NotEqual(firstUrl, vm.CurrentMusicVideoPath);
        Assert.Contains("id=dQw4w9WgXcQ", vm.CurrentMusicVideoPath);
        Assert.True(vm.IsStreamingMusicVideo);
        Assert.Equal(2, source.Resolves.Count);
    }

    [AvaloniaFact]
    public async Task Player_FollowsAChoiceMadeInTheDialog()
    {
        var (service, _, _) = MakeService();
        var vm = Player(service, autoFind: false);
        var track = Rick();
        vm.CurrentTrack = track;
        await vm.MusicVideoStreamTask;
        Assert.False(vm.IsStreamingMusicVideo);

        var picker = new MusicVideoPickerViewModel(track, service);
        picker.LinkText = "https://youtu.be/LM8dsLtUs2A?t=3";
        await picker.LinkLookup;
        Assert.Equal("LM8dsLtUs2A", picker.LinkPreview?.Info.Id);
        picker.StreamLinkCommand.Execute(null);
        await vm.MusicVideoStreamTask;

        Assert.True(vm.IsStreamingMusicVideo);
        Assert.Contains("id=LM8dsLtUs2A", vm.CurrentMusicVideoPath);

        new MusicVideoPickerViewModel(track, service).NoVideoCommand.Execute(null);
        await vm.MusicVideoStreamTask;
        Assert.False(vm.IsStreamingMusicVideo);
        Assert.Null(vm.CurrentMusicVideoPath);
    }

    [AvaloniaFact]
    public async Task Picker_RejectsANonLink_AndRanksTheSearch()
    {
        var (service, _, _) = MakeService();
        var picker = new MusicVideoPickerViewModel(Rick(), service);
        picker.LinkText = "never gonna give you up";
        await picker.LinkLookup;
        Assert.Null(picker.LinkPreview);
        Assert.True(picker.HasLinkStatus);

        await picker.SearchCommand.ExecuteAsync(null);
        Assert.Equal("dQw4w9WgXcQ", picker.Results[0].Info.Id);
    }
}
