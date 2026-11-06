using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;
using static Noctis.Services.ITunesArtworkService;

namespace Noctis.Tests;

/// <summary>
/// GitHub #185 ("have option to automatically download animated artwork at 2160 by 2160"):
/// Settings › Artwork › Animated Artwork Quality picks the loop the fetch on opening an album
/// and Find Animated Artwork download, falling back to 1080p; and the owner's ask with it, a
/// download bar in the Edit Info artwork and animated artwork pop-ups that the pop-up closing
/// cancels. No network: Apple is stubbed (AnimatedArtworkChooserTests.Apple).
/// </summary>
public class AnimatedArtworkQualityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "animquality-" + Guid.NewGuid().ToString("N"));

    private readonly ITestOutputHelper _out;

    public AnimatedArtworkQualityTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static AnimatedArtworkVariant V(string label, int size)
        => new(label, $"https://mvod.itunes.apple.com/{label}.m3u8", size, size, "hvc1", size, true);

    // ── Which loop is picked ──

    [Fact]
    public void PickVariant_FollowsTheSetting_AndFallsBackToWhatTheAlbumHas()
    {
        var both = new[] { V("1080p", 1080), V("2160p", 2160) };
        Assert.Equal("1080p", AnimatedArtworkBulkService.PickVariant(both, preferMax: false).Label);
        Assert.Equal("2160p", AnimatedArtworkBulkService.PickVariant(both, preferMax: true).Label);

        // No 2160p loop: 1080p. No 1080p loop: the 2160p one rather than nothing (as before).
        Assert.Equal("1080p", AnimatedArtworkBulkService.PickVariant(new[] { V("1080p", 1080) }, preferMax: true).Label);
        Assert.Equal("2160p", AnimatedArtworkBulkService.PickVariant(new[] { V("2160p", 2160) }, preferMax: false).Label);
        // Only a lower-res fallback tier ("720p"): that one either way.
        Assert.Equal("720p", AnimatedArtworkBulkService.PickVariant(new[] { V("720p", 720) }, preferMax: true).Label);
    }

    /// <summary>The Apple stub, with the 2160p line taken out of every master playlist when asked.</summary>
    private static HttpClient Http(AnimatedArtworkChooserTests.Apple apple, bool no2160 = false)
        => new(new AnimatedArtworkChooserTests.StubHandler(async req =>
        {
            var resp = await apple.Respond(req);
            var url = req.RequestUri!.ToString();
            if (no2160 && url.EndsWith("_default.m3u8", StringComparison.Ordinal) && resp.IsSuccessStatusCode)
            {
                var lines = (await resp.Content.ReadAsStringAsync()).Split('\n').ToList();
                var at = lines.FindIndex(l => l.Contains("RESOLUTION=2160x2160", StringComparison.Ordinal));
                if (at >= 0) lines.RemoveRange(at, 2);
                resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Join('\n', lines), Encoding.UTF8) };
            }
            return resp;
        }));

    private static IReadOnlyList<Track> ShortNSweet()
    {
        var id = Guid.NewGuid();
        return new[]
        {
            new Track { Id = Guid.NewGuid(), Title = "Taste", Album = "Short n' Sweet", AlbumArtist = "Sabrina Carpenter",
                Artist = "Sabrina Carpenter", AlbumId = id, Year = 2024, SourceType = SourceType.Local,
                FilePath = Path.Combine(Path.GetTempPath(), "nowhere", "01.mp3") },
        };
    }

    private AnimatedArtworkAutoFetcher Fetcher(HttpClient http)
    {
        var persistence = new PersistenceService(_dir);
        var covers = new AnimatedCoverService(persistence);
        var clock = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        return new AnimatedArtworkAutoFetcher(
            AnimatedArtworkBulkService.For(new ITunesArtworkService(http), covers, ledgerPath: null),
            covers,
            new AnimatedArtworkCache(persistence, Path.Combine(_dir, "blocked.json"), () => clock),
            Path.Combine(_dir, "misses.json"),
            () => clock, (t, _) => { clock += t; return Task.CompletedTask; });
    }

    [Theory]
    [InlineData(false, false, "_1080.m3u8")]
    [InlineData(true, false, "_2160.m3u8")]
    [InlineData(true, true, "_1080.m3u8")] // the album has no 2160p loop: 1080p, not nothing
    public async Task AutoFetch_DownloadsTheLoopTheSettingAsksFor(bool preferMax, bool no2160, string expected)
    {
        var apple = new AnimatedArtworkChooserTests.Apple();
        var album = ShortNSweet();

        var path = await Fetcher(Http(apple, no2160)).FetchAsync(album, 1L << 30, preferMax, CancellationToken.None);

        Assert.NotNull(path);
        Assert.Equal("fake-mp4-bytesfake-mp4-bytes", File.ReadAllText(path!));
        var playlists = apple.Requests.Where(u => u.EndsWith("_1080.m3u8", StringComparison.Ordinal)
                                                  || u.EndsWith("_2160.m3u8", StringComparison.Ordinal)).ToList();
        Assert.Single(playlists);
        Assert.EndsWith(expected, playlists[0]);
    }

    [Fact]
    public async Task BulkRun_ReadsTheSettingPerAlbum()
    {
        var picked = new List<string>();
        var preferMax = false;
        var covers = new AnimatedCoverService(new PersistenceService(_dir));
        var clock = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        var svc = new AnimatedArtworkBulkService(
            (q, _) => Task.FromResult<IReadOnlyList<RankedAlbumCandidate>>(new[]
                { new RankedAlbumCandidate(new ArtworkCandidate(1, q.Album, q.Artist, "", "", "", "https://music.apple.com/us/album/x/1"), 140) }),
            (_, _) => Task.FromResult<AnimatedArtworkAlbum?>(new AnimatedArtworkAlbum(1, "t", "", "", null, null,
                new[] { V("1080p", 1080), V("2160p", 2160) }, null, HasSquare: true, HasTall: false)),
            (v, path, _) =>
            {
                picked.Add(v.Label);
                preferMax = true; // switched in Settings while the run goes on
                File.WriteAllText(path, "mp4");
                return Task.FromResult(true);
            },
            covers, ledgerPath: null)
        {
            Clock = () => clock,
            Delay = (t, _) => { clock += t; return Task.CompletedTask; },
            PreferMaxQuality = () => preferMax,
        };

        Track T(string album) => new() { Title = "t", Album = album, AlbumArtist = "X", Artist = "X", AlbumId = Guid.NewGuid(),
            SourceType = SourceType.Local, FilePath = Path.Combine(_dir, album + ".mp3") };
        await svc.RunAsync(new[] { new[] { T("A") }, new[] { T("B") } }, recheck: false, null, CancellationToken.None);

        Assert.Equal(new[] { "1080p", "2160p" }, picked);
    }

    [Fact]
    public void Cache_EvictsOldestLoops_WhenBigLoopsPassTheCap()
    {
        // 2160p loops are ~3x the size: the cap is bytes, not a count, so a big newcomer pushes
        // out as many old loops as it takes and stays itself.
        var persistence = new PersistenceService(_dir);
        var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        var cache = new AnimatedArtworkCache(persistence, null, () => now);
        Directory.CreateDirectory(cache.Folder);
        Guid Put(int bytes, int minutesAgo)
        {
            var id = Guid.NewGuid();
            File.WriteAllBytes(cache.PathFor(id), new byte[bytes]);
            File.SetLastWriteTimeUtc(cache.PathFor(id), now.AddMinutes(-minutesAgo));
            return id;
        }
        var old1080 = Put(30, 30);
        var mid1080 = Put(30, 20);
        var recent2160 = Put(90, 10);
        var part = cache.PartPathFor(Guid.NewGuid());
        File.WriteAllBytes(part, new byte[90]);
        var newest = cache.Commit(Guid.NewGuid(), part);

        Assert.Equal(2, cache.Trim(200));

        Assert.False(File.Exists(cache.PathFor(old1080)));
        Assert.False(File.Exists(cache.PathFor(mid1080)));
        Assert.True(File.Exists(cache.PathFor(recent2160)));
        Assert.True(File.Exists(newest));
        Assert.Equal(180, cache.SizeBytes());
    }

    // ── Settings ──

    private sealed class NoHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task Setting_DefaultsTo1080p_Persists_AndResets()
    {
        var persistence = new PersistenceService(_dir);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoHistory());
        await vm.LoadAsync();
        Assert.Equal("1080p", vm.AnimatedArtworkQuality);
        Assert.False(vm.AnimatedArtworkPrefersMax);
        Assert.Equal(new[] { "1080p", "2160p (Max)" }, vm.AnimatedArtworkQualityOptions.Select(o => o.Label));

        vm.SelectedAnimatedArtworkQualityOption = vm.AnimatedArtworkQualityOptions[1];
        vm.SelectedAnimatedArtworkQualityOption = null; // a ComboBox rebuilding its items
        Assert.True(vm.AnimatedArtworkPrefersMax);
        await vm.SaveAsync();
        Assert.Equal("2160p", (await persistence.LoadSettingsAsync()).AnimatedArtworkQuality);

        var reloaded = new SettingsViewModel(persistence, new FakeLibraryService(), new NoHistory());
        await reloaded.LoadAsync();
        Assert.Equal("2160p", reloaded.AnimatedArtworkQuality);
        Assert.Same(reloaded.AnimatedArtworkQualityOptions[1], reloaded.SelectedAnimatedArtworkQualityOption);

        reloaded.ResetSettingsToDefaults(new AppSettings());
        Assert.Equal("1080p", reloaded.AnimatedArtworkQuality);
        Assert.False(reloaded.AnimatedArtworkPrefersMax);
    }

    [Theory]
    [InlineData(null, "1080p")]
    [InlineData("", "1080p")]
    [InlineData("4k", "1080p")]
    [InlineData(" 2160P ", "2160p")]
    public void HandEditedSetting_Snaps(string? stored, string expected)
        => Assert.Equal(expected, AnimatedArtworkQualities.Normalize(stored));

    // ── The pop-ups' download bar ──

    /// <summary>A body served in two halves: the second only once <see cref="Release"/> is set.
    /// No Content-Length when <c>withLength</c> is off.</summary>
    private sealed class HalvesStream(byte[] body, TaskCompletionSource release) : Stream
    {
        private int _pos;
        public bool Cancelled { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var half = body.Length / 2;
            if (_pos == half)
            {
                try { await release.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            var end = _pos < half ? half : body.Length;
            var n = Math.Min(buffer.Length, end - _pos);
            body.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }

    private sealed class Served
    {
        public HalvesStream? Stream;
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The Apple stub, except <paramref name="match"/> answers with a two-half body.</summary>
    private static HttpClient Slow(AnimatedArtworkChooserTests.Apple apple, Func<string, bool> match, byte[] body, bool withLength, Served served)
        => new(new AnimatedArtworkChooserTests.StubHandler(req =>
        {
            if (!match(req.RequestUri!.ToString())) return apple.Respond(req);
            served.Stream = new HalvesStream(body, served.Release);
            var content = new StreamContent(served.Stream);
            if (withLength) content.Headers.ContentLength = body.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));

    private static MetadataViewModel Vm(HttpClient http)
    {
        var track = new Track { Title = "Taste", Artist = "Sabrina Carpenter", AlbumArtist = "Sabrina Carpenter",
            Album = "Short n' Sweet", FilePath = "C:\\nope.mp3" };
        return new MetadataViewModel(track, new MetadataService(), new FakeLibraryService(), new TestPersistenceService(),
            new FakeAnimatedCoverService(), itunes: new ITunesArtworkService(http));
    }

    private static bool SpinUntil(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    private static bool IsLoopSegment(string url) => url.EndsWith("explicit_2160_seg.mp4", StringComparison.Ordinal);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnimatedPick_ShowsProgress_ThenApplies(bool withLength)
    {
        var apple = new AnimatedArtworkChooserTests.Apple();
        var served = new Served();
        var body = new byte[4 << 20];
        var vm = Vm(Slow(apple, IsLoopSegment, body, withLength, served));
        await vm.SearchAnimatedCoverCommand.ExecuteAsync(null);
        var choice = vm.AnimatedArtworkSearchResults[0].Choices.Single(c => c.Label == "2160p");

        var pick = vm.SelectAnimatedArtworkResultCommand.ExecuteAsync(choice);
        var bar = vm.AnimatedArtworkDownload;
        Assert.True(vm.IsDownloadingAnimatedCover);
        Assert.True(bar.IsActive);
        // Half has come, then the server stalls: the bar shows it (a report inside the update
        // pace is held and shown, not dropped).
        Assert.True(SpinUntil(() => withLength ? bar.Fraction >= 0.45 : bar.Detail.StartsWith("2.0 MB", StringComparison.Ordinal)), bar.Detail);

        if (withLength)
        {
            // The init part (14 bytes) plus half of the 4 MB segment, of the whole.
            Assert.False(bar.IsIndeterminate);
            Assert.InRange(bar.Fraction, 0.45, 0.55);
            Assert.StartsWith("50%", bar.Detail);
            Assert.Equal("50%", bar.PercentText);
        }
        else
        {
            // No length: the bar runs without a fill and says how much has come.
            Assert.True(bar.IsIndeterminate);
            Assert.Equal(string.Empty, bar.PercentText);
            Assert.StartsWith("2.0 MB", bar.Detail);
        }

        served.Release.SetResult();
        await pick;

        Assert.True(vm.HasAnimatedCover, vm.AnimatedSearchStatus);
        Assert.False(bar.IsActive);
        Assert.False(vm.IsDownloadingAnimatedCover);
        Assert.Equal(14 + body.Length, new FileInfo(vm.AnimatedCoverPath!).Length);
        File.Delete(vm.AnimatedCoverPath!);
    }

    [Fact]
    public async Task ClosingTheAnimatedPopUp_StopsTheDownload_AndAppliesNothing()
    {
        var apple = new AnimatedArtworkChooserTests.Apple();
        var served = new Served();
        var vm = Vm(Slow(apple, IsLoopSegment, new byte[1 << 20], withLength: true, served));
        await vm.SearchAnimatedCoverCommand.ExecuteAsync(null);
        var choice = vm.AnimatedArtworkSearchResults[0].Choices.Single(c => c.Label == "2160p");

        var pick = vm.SelectAnimatedArtworkResultCommand.ExecuteAsync(choice);
        Assert.True(SpinUntil(() => vm.AnimatedArtworkDownload.Fraction > 0));

        vm.CloseAnimatedArtworkSearchCommand.Execute(null);
        await pick.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(served.Stream!.Cancelled);
        Assert.False(vm.HasAnimatedCover);
        Assert.False(vm.IsDownloadingAnimatedCover);
        Assert.False(vm.AnimatedArtworkDownload.IsActive);
        Assert.Equal(string.Empty, vm.AnimatedSearchStatus); // a cancel is not an error
    }

    [Fact]
    public async Task CancelledLoopDownload_LeavesNoPartialFile()
    {
        var apple = new AnimatedArtworkChooserTests.Apple();
        var served = new Served();
        var itunes = new ITunesArtworkService(Slow(apple, IsLoopSegment, new byte[1 << 20], withLength: true, served));
        var variant = new AnimatedArtworkVariant("2160p",
            "https://mvod.itunes.apple.com/itunes-assets/HLSVideo211/v4/explicit_2160.m3u8", 2160, 2160, "hvc1", 1, true);
        var dest = Path.Combine(_dir, "loop.mp4");
        var reports = new List<(long Done, long Total)>();
        using var cts = new CancellationTokenSource();

        var download = itunes.DownloadHlsVariantAsMp4Async(variant, dest, cts.Token, (d, t) => { lock (reports) reports.Add((d, t)); });
        Assert.True(SpinUntil(() => { lock (reports) return reports.Any(r => r.Done > 14); }));
        Assert.True(File.Exists(dest));
        cts.Cancel();

        Assert.False(await download);
        Assert.False(File.Exists(dest));
        // The init part's length is not the loop's: only the last part's length gives a total.
        lock (reports)
        {
            Assert.Equal((0L, 0L), reports[0]);
            Assert.Equal((14L, 0L), reports[1]);
            Assert.Equal((14L, 14L + (1 << 20)), reports[2]);
            Assert.All(reports.Skip(2), r => Assert.Equal(14 + (1 << 20), r.Total));
        }
    }

    [Fact]
    public async Task ClosingTheArtworkPopUp_StopsTheCoverDownload()
    {
        var apple = new AnimatedArtworkChooserTests.Apple();
        var served = new Served();
        var http = Slow(apple, u => u.Contains("/Music/", StringComparison.Ordinal) && !u.Contains("100x100", StringComparison.Ordinal),
            new byte[1 << 20], withLength: true, served);
        var vm = Vm(http);
        var candidate = new ArtworkCandidate(1, "Short n' Sweet", "Sabrina Carpenter",
            "https://is1-ssl.mzstatic.com/image/thumb/Music/1.jpg/100x100bb.jpg",
            "https://is1-ssl.mzstatic.com/image/thumb/Music/1.jpg/1000x1000bb.jpg",
            "https://is1-ssl.mzstatic.com/image/thumb/Music/1.jpg/10000x10000-999.jpg",
            "https://music.apple.com/us/album/x/1");
        vm.IsArtworkSearchOpen = true;

        var pick = vm.SelectStandardArtworkResultCommand.ExecuteAsync(new ArtworkSearchResult(candidate, null));
        Assert.True(SpinUntil(() => vm.ArtworkDownload.Fraction >= 0.45), vm.ArtworkDownload.Detail);
        Assert.False(vm.ArtworkDownload.IsIndeterminate);
        Assert.StartsWith("50%", vm.ArtworkDownload.Detail);

        vm.CloseArtworkSearchCommand.Execute(null);
        await pick.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(served.Stream!.Cancelled);
        Assert.False(vm.IsDownloadingArtwork);
        Assert.False(vm.ArtworkDownload.IsActive);
        Assert.Equal(string.Empty, vm.ArtworkSearchStatus);
        // The thumbnail fallback was not tried after the cancel.
        Assert.DoesNotContain(apple.Requests, u => u.Contains("100x100", StringComparison.Ordinal));
    }

    [Fact]
    public void Bar_ReportsAreThrottled_ButTheLastAlwaysLands()
    {
        var now = TimeSpan.Zero;
        var bar = new ArtworkDownloadProgress(() => now);
        bar.Begin();
        bar.Report(0, 1000);
        now += TimeSpan.FromMilliseconds(50);
        bar.Report(400, 1000); // within 250 ms of the last update: dropped
        Assert.Equal(0, bar.Fraction);
        bar.Report(1000, 1000); // done: always shown
        Assert.Equal(1, bar.Fraction);
        Assert.Equal("100%", bar.PercentText);
        bar.End();
        bar.Report(10, 1000); // after the end: ignored
        Assert.False(bar.IsActive);
        Assert.Equal(1, bar.Fraction);
    }

    /// <summary>Live check of the 1080p vs 2160p loop sizes behind the hint ("about 3x").
    /// Network: off unless NOCTIS_LIVE_ANIMATED=1.</summary>
    [Fact]
    public async Task Live_2160pLoopIsAboutThreeTimesThe1080p()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_LIVE_ANIMATED") != "1") return;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var itunes = new ITunesArtworkService(http);
        var album = await itunes.GetAnimatedArtworkAlbumAsync("https://music.apple.com/us/album/short-n-sweet/1750307020");
        Assert.NotNull(album);
        var sizes = new Dictionary<string, long>();
        foreach (var v in album!.Variants)
        {
            var dest = Path.Combine(_dir, v.Label + ".mp4");
            long last = 0;
            Assert.True(await itunes.DownloadHlsVariantAsMp4Async(v, dest, default, (d, _) => last = d));
            sizes[v.Label] = new FileInfo(dest).Length;
            Assert.Equal(sizes[v.Label], last);
        }
        _out.WriteLine(string.Join(", ", sizes.Select(kv => $"{kv.Key} {kv.Value / 1048576.0:0.0} MB")));
        Assert.InRange(sizes["2160p"] / (double)sizes["1080p"], 1.5, 6);
    }
}
