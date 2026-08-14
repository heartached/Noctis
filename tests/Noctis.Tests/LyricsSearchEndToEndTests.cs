using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// End-to-end coverage of the online lyrics search path in
/// <see cref="LyricsViewModel"/>: provider arbitration (synced beats unsynced,
/// LRCLIB wins ties), the provider-error / instrumental / not-found message
/// precedence, RemoveLyrics vs the app-written sidecar registry, "Try alternate"
/// sidecar replacement rules, and the stale-result generation guard.
/// Short-circuit behaviors (instrumental /get, Unknown Artist) are pinned by
/// <see cref="LyricsSearchShortCircuitTests"/>; candidate validation by
/// <see cref="LyricsSearchSelectorTests"/> — not duplicated here.
/// </summary>
public class LyricsSearchEndToEndTests
{
    // ── Harness ──

    private sealed class StubLrcLib : ILrcLibService
    {
        public Func<string, Task<LrcLibResult?>> GetImpl = _ => Task.FromResult<LrcLibResult?>(null);
        public Func<string, Task<List<LrcLibResult>>> SearchImpl = _ => Task.FromResult(new List<LrcLibResult>());
        public int GetCalls;
        public int SearchCalls;

        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        {
            Interlocked.Increment(ref GetCalls);
            return GetImpl(artist);
        }

        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SearchCalls);
            return SearchImpl(artist);
        }
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Func<Task<LrcLibResult?>> Impl = () => Task.FromResult<LrcLibResult?>(null);
        public int Calls;

        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Impl();
        }
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private static Task<LrcLibResult?> FromResult(LrcLibResult? result) => Task.FromResult(result);

    private static LrcLibResult Result(
        string? synced = null, string? plain = null, bool instrumental = false,
        string track = "Test Song", string artist = "Test Artist", double duration = 200) => new()
    {
        TrackName = track,
        ArtistName = artist,
        Duration = duration,
        SyncedLyrics = synced,
        PlainLyrics = plain,
        Instrumental = instrumental,
    };

    private static LyricsProviderException ProviderError() =>
        new("LRCLIB", new TimeoutException("timed out"));

    /// <summary>Unique per test: displayed online lyrics auto-persist an .lrc next to
    /// this path, which a later test's local probe would otherwise find.</summary>
    private static string TempTrackPath() =>
        Path.Combine(Path.GetTempPath(), $"noctis-e2e-{Guid.NewGuid():N}.mp3");

    private static (LyricsViewModel Vm, PlayerViewModel Player, Track Track) Mount(
        StubLrcLib lrcLib, StubNetEase netEase, string? filePath = null, string artist = "Test Artist")
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(
            player, lrcLib, netEase, new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());

        var track = new Track
        {
            Title = "Test Song",
            Artist = artist,
            Duration = TimeSpan.FromSeconds(200),
            FilePath = filePath ?? TempTrackPath(),
        };
        player.CurrentTrack = track;
        return (vm, player, track);
    }

    /// <summary>Pumps the UI thread until the condition holds, with a wall-clock
    /// budget like the other lyrics harnesses (the pool may be busy in a full run).</summary>
    private static async Task PumpUntilAsync(Func<bool> done, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !done())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static Func<bool> SearchSettled(LyricsViewModel vm, StubLrcLib lrcLib, StubNetEase netEase) =>
        () => lrcLib.GetCalls + lrcLib.SearchCalls + netEase.Calls > 0 && !vm.IsSearching;

    private static string SafeRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
        catch { return string.Empty; }
    }

    private static void Cleanup(string trackPath)
    {
        var lrcPath = Path.ChangeExtension(trackPath, ".lrc");
        try { if (File.Exists(lrcPath)) File.Delete(lrcPath); } catch { }
        AppWrittenSidecarRegistry.Default.Remove(lrcPath);
    }

    // ── Provider arbitration (PickBestResult through the VM) ──

    [AvaloniaFact]
    public async Task NetEaseSynced_BeatsLrcLibPlain_LrcLibKeptAsAlternate()
    {
        var lrcLib = new StubLrcLib { GetImpl = _ => FromResult(Result(plain: "plain words")) };
        var netEase = new StubNetEase { Impl = () => FromResult(Result(synced: "[00:01.00]synced words")) };
        var (vm, _, track) = Mount(lrcLib, netEase);

        try
        {
            vm.SearchLyricsForTrack(track);
            await PumpUntilAsync(SearchSettled(vm, lrcLib, netEase));

            Assert.Equal("NetEase", vm.LyricsSourceName);
            Assert.True(vm.IsSynced);
            Assert.True(vm.HasAlternateLyrics);
            Assert.Equal("Try LRCLIB", vm.AlternateLyricsLabel);
            Assert.Equal(string.Empty, vm.SearchFailedMessage);
        }
        finally { Cleanup(track.FilePath); }
    }

    [AvaloniaFact]
    public async Task BothProvidersSynced_LrcLibWinsTie()
    {
        var lrcLib = new StubLrcLib { GetImpl = _ => FromResult(Result(synced: "[00:01.00]curated")) };
        var netEase = new StubNetEase { Impl = () => FromResult(Result(synced: "[00:01.00]scraped")) };
        var (vm, _, track) = Mount(lrcLib, netEase);

        try
        {
            vm.SearchLyricsForTrack(track);
            await PumpUntilAsync(SearchSettled(vm, lrcLib, netEase));

            Assert.Equal("LRCLIB", vm.LyricsSourceName);
            Assert.True(vm.HasAlternateLyrics);
            Assert.Equal("Try NetEase", vm.AlternateLyricsLabel);
        }
        finally { Cleanup(track.FilePath); }
    }

    // ── Message precedence: instrumental > provider error > not found ──

    [AvaloniaFact]
    public async Task AllProvidersErrored_ShowsConnectionMessage_NotNoLyricsFound()
    {
        var lrcLib = new StubLrcLib { GetImpl = _ => Task.FromException<LrcLibResult?>(ProviderError()) };
        var netEase = new StubNetEase { Impl = () => Task.FromException<LrcLibResult?>(ProviderError()) };
        var (vm, _, track) = Mount(lrcLib, netEase);

        vm.SearchLyricsForTrack(track);
        await PumpUntilAsync(SearchSettled(vm, lrcLib, netEase));

        Assert.Equal("Search failed — check your internet connection.", vm.SearchFailedMessage);
        Assert.True(vm.ShowSearchButton);
        Assert.Empty(vm.LyricLines);
    }

    [AvaloniaFact]
    public async Task NetEaseErrored_LrcLibAnsweredEmpty_ShowsNoLyricsFound()
    {
        // A provider that answered — even empty-handed — proves the connection
        // works. Blaming the internet here made every LRCLIB miss read as a
        // network failure whenever the other provider was down or unusable.
        var lrcLib = new StubLrcLib(); // get null, search empty — a definitive answer
        var netEase = new StubNetEase { Impl = () => Task.FromException<LrcLibResult?>(ProviderError()) };
        var (vm, _, track) = Mount(lrcLib, netEase);

        vm.SearchLyricsForTrack(track);
        await PumpUntilAsync(SearchSettled(vm, lrcLib, netEase));

        Assert.Equal("No Lyrics found.", vm.SearchFailedMessage);
        Assert.True(vm.ShowSearchButton);
        Assert.Empty(vm.LyricLines);
    }

    [AvaloniaFact]
    public async Task LrcLibErrored_NetEaseAnsweredEmpty_ShowsNoLyricsFound()
    {
        var lrcLib = new StubLrcLib { GetImpl = _ => Task.FromException<LrcLibResult?>(ProviderError()) };
        var netEase = new StubNetEase(); // definitive miss
        var (vm, _, track) = Mount(lrcLib, netEase);

        vm.SearchLyricsForTrack(track);
        await PumpUntilAsync(SearchSettled(vm, lrcLib, netEase));

        Assert.Equal("No Lyrics found.", vm.SearchFailedMessage);
        Assert.True(vm.ShowSearchButton);
        Assert.Empty(vm.LyricLines);
    }

    [AvaloniaFact]
    public async Task ProviderError_OtherProviderHasResults_ShowsThoseResults()
    {
        var lrcLib = new StubLrcLib { GetImpl = _ => Task.FromException<LrcLibResult?>(ProviderError()) };
        var netEase = new StubNetEase { Impl = () => FromResult(Result(synced: "[00:01.00]still works")) };
        var (vm, _, track) = Mount(lrcLib, netEase);

        try
        {
            vm.SearchLyricsForTrack(track);
            await PumpUntilAsync(SearchSettled(vm, lrcLib, netEase));

            Assert.NotEmpty(vm.LyricLines);
            Assert.Equal("NetEase", vm.LyricsSourceName);
            Assert.False(vm.HasAlternateLyrics);
            Assert.Equal(string.Empty, vm.SearchFailedMessage);
        }
        finally { Cleanup(track.FilePath); }
    }

    [AvaloniaFact]
    public async Task BothProvidersMiss_ShowsNoLyricsFound()
    {
        var lrcLib = new StubLrcLib(); // get null, search empty
        var netEase = new StubNetEase();
        var (vm, _, track) = Mount(lrcLib, netEase);

        vm.SearchLyricsForTrack(track);
        await PumpUntilAsync(SearchSettled(vm, lrcLib, netEase));

        Assert.Equal("No Lyrics found.", vm.SearchFailedMessage);
        Assert.True(vm.ShowSearchButton);
    }

    [AvaloniaFact]
    public async Task Instrumental_OutranksProviderError()
    {
        var lrcLib = new StubLrcLib { GetImpl = _ => FromResult(Result(instrumental: true)) };
        var netEase = new StubNetEase { Impl = () => Task.FromException<LrcLibResult?>(ProviderError()) };
        var (vm, _, track) = Mount(lrcLib, netEase);

        vm.SearchLyricsForTrack(track);
        await PumpUntilAsync(SearchSettled(vm, lrcLib, netEase));

        // The exact /get said "instrumental" — a definitive answer, not a network
        // condition, so the connection message must not mask it.
        Assert.Equal("This track is instrumental.", vm.SearchFailedMessage);
        Assert.Equal(0, lrcLib.SearchCalls);
        Assert.Empty(vm.LyricLines);
    }

    // ── RemoveLyrics vs the app-written sidecar registry ──

    [AvaloniaFact]
    public async Task RemoveLyrics_DeletesAppWrittenSidecar_CacheAndTrackFields()
    {
        var lrcLib = new StubLrcLib { GetImpl = _ => FromResult(Result(synced: "[00:01.00]from the app")) };
        var netEase = new StubNetEase();
        var (vm, _, track) = Mount(lrcLib, netEase);
        var lrcPath = Path.ChangeExtension(track.FilePath, ".lrc");
        var cacheLrc = Path.Combine(AppPaths.DataRoot, "lyrics_cache", $"{track.Id}.lrc");

        try
        {
            vm.SearchLyricsForTrack(track);
            // The sidecar/cache writes are fire-and-forget off DisplayOnlineLyrics —
            // wait for both to land (and for the registry entry) before removing.
            await PumpUntilAsync(() => File.Exists(lrcPath) && File.Exists(cacheLrc)
                && AppWrittenSidecarRegistry.Default.Contains(lrcPath));

            Assert.True(File.Exists(lrcPath));
            Assert.True(AppWrittenSidecarRegistry.Default.Contains(lrcPath));
            Assert.True(vm.CanRemoveLyrics);

            await vm.RemoveLyricsCommand.ExecuteAsync(null);

            Assert.False(AppWrittenSidecarRegistry.Default.Contains(lrcPath));
            Assert.False(File.Exists(cacheLrc));
            Assert.Equal(string.Empty, track.Lyrics);
            Assert.Equal(string.Empty, track.SyncedLyrics);
            Assert.Empty(vm.LyricLines);
            Assert.True(vm.ShowSearchButton);
            Assert.False(vm.CanRemoveLyrics);

            // The physical delete goes through the OS trash, whose backends are
            // environment-dependent on CI (Finder / gio); assert it where it is
            // deterministic. The registry gate above is the cross-platform contract.
            if (OperatingSystem.IsWindows())
                Assert.False(File.Exists(lrcPath));
        }
        finally
        {
            Cleanup(track.FilePath);
            try { if (File.Exists(cacheLrc)) File.Delete(cacheLrc); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task RemoveLyrics_LeavesUserOwnedSidecarAlone()
    {
        var lrcLib = new StubLrcLib();
        var netEase = new StubNetEase();
        var (vm, _, track) = Mount(lrcLib, netEase);
        var lrcPath = Path.ChangeExtension(track.FilePath, ".lrc");
        const string userContent = "[00:05.00]my own hand-timed line";
        File.WriteAllText(lrcPath, userContent);

        try
        {
            vm.SearchLyricsForTrack(track);
            await PumpUntilAsync(() => vm.LyricLines.Count > 0);

            // The probe found the user's sidecar; no online search ever ran.
            Assert.Equal(0, lrcLib.GetCalls + lrcLib.SearchCalls + netEase.Calls);

            await vm.RemoveLyricsCommand.ExecuteAsync(null);

            // Not in the registry → not ours → never deleted on the user's behalf.
            Assert.True(File.Exists(lrcPath));
            Assert.Equal(userContent, File.ReadAllText(lrcPath));
        }
        finally { Cleanup(track.FilePath); }
    }

    // ── "Try alternate" sidecar replacement rules ──

    [AvaloniaFact]
    public async Task SwitchToAlternate_OverwritesAppWrittenSidecar()
    {
        var lrcLib = new StubLrcLib { GetImpl = _ => FromResult(Result(synced: "[00:01.00]primary lyrics")) };
        var netEase = new StubNetEase { Impl = () => FromResult(Result(synced: "[00:02.00]alternate lyrics")) };
        var (vm, _, track) = Mount(lrcLib, netEase);
        var lrcPath = Path.ChangeExtension(track.FilePath, ".lrc");

        try
        {
            vm.SearchLyricsForTrack(track);
            // Wait for the app's own fire-and-forget sidecar write to land AND register
            // before switching. (The switch-before-the-write-lands race is pinned by
            // SwitchToAlternate_BeforePrimarySidecarWriteLands_AlternateStillWins.)
            await PumpUntilAsync(() => SafeRead(lrcPath).Contains("primary lyrics")
                && AppWrittenSidecarRegistry.Default.Contains(lrcPath));
            Assert.Equal("LRCLIB", vm.LyricsSourceName);
            Assert.True(vm.HasAlternateLyrics);

            vm.SwitchToAlternateLyricsCommand.Execute(null);
            await PumpUntilAsync(() => SafeRead(lrcPath).Contains("alternate lyrics"));

            // The explicit choice must stick on disk, or the old sidecar out-prioritizes
            // the cache on the next play and the switch silently reverts.
            Assert.Contains("alternate lyrics", SafeRead(lrcPath));
            Assert.Equal("NetEase", vm.LyricsSourceName);
            Assert.Equal("Try LRCLIB", vm.AlternateLyricsLabel);
            Assert.Equal("[00:02.00]alternate lyrics", track.SyncedLyrics);
        }
        finally { Cleanup(track.FilePath); }
    }

    [AvaloniaFact]
    public async Task SwitchToAlternate_NeverOverwritesUserOwnedSidecar()
    {
        var lrcLib = new StubLrcLib { GetImpl = _ => FromResult(Result(synced: "[00:01.00]primary lyrics")) };
        var netEase = new StubNetEase { Impl = () => FromResult(Result(synced: "[00:02.00]alternate lyrics")) };
        var (vm, _, track) = Mount(lrcLib, netEase);
        var lrcPath = Path.ChangeExtension(track.FilePath, ".lrc");
        const string userContent = "[00:05.00]my own hand-timed line";
        File.WriteAllText(lrcPath, userContent);

        try
        {
            // The probe finds the user's sidecar, so no auto-search; run one manually.
            vm.SearchLyricsForTrack(track);
            await PumpUntilAsync(() => vm.LyricLines.Count > 0);

            vm.SearchLyricsCommand.Execute(null);
            await PumpUntilAsync(SearchSettled(vm, lrcLib, netEase));
            Assert.Equal("LRCLIB", vm.LyricsSourceName);
            Assert.True(vm.HasAlternateLyrics);

            vm.SwitchToAlternateLyricsCommand.Execute(null);
            // Negative case: give the (skipping) gated write task time to run.
            await Task.Delay(300);
            Dispatcher.UIThread.RunJobs();

            // File untouched, and the blocked switch left the track fields on the
            // primary result — they must not advertise lyrics disk will override.
            Assert.Equal(userContent, File.ReadAllText(lrcPath));
            Assert.Equal("[00:01.00]primary lyrics", track.SyncedLyrics);
            // The switched-to lyrics still display for this session.
            Assert.Equal("NetEase", vm.LyricsSourceName);
        }
        finally { Cleanup(track.FilePath); }
    }

    // ── Writer-lane races: switch/remove vs in-flight fire-and-forget writes ──

    /// <summary>Parks the writer lane on a blocking first item; disposing releases it.
    /// Everything enqueued while parked stays queued in FIFO order.</summary>
    private sealed class ParkedWriterLane : IDisposable
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _blocker;

        public ParkedWriterLane() =>
            _blocker = LyricsViewModel.EnqueueLyricsFileWork(() => _release.Task.Wait());

        /// <summary>Releases the lane after <paramref name="delayMs"/> on a background
        /// thread — for callers that are about to block the UI thread on the lane.</summary>
        public void ReleaseAfter(int delayMs) =>
            _ = Task.Run(async () => { await Task.Delay(delayMs); _release.TrySetResult(); });

        public void Dispose()
        {
            _release.TrySetResult();
            try { _blocker.Wait(1000); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task SwitchToAlternate_BeforePrimarySidecarWriteLands_AlternateStillWins()
    {
        var lrcLib = new StubLrcLib { GetImpl = _ => FromResult(Result(synced: "[00:01.00]primary lyrics")) };
        var netEase = new StubNetEase { Impl = () => FromResult(Result(synced: "[00:02.00]alternate lyrics")) };
        var (vm, _, track) = Mount(lrcLib, netEase);
        var lrcPath = Path.ChangeExtension(track.FilePath, ".lrc");

        try
        {
            // Park the writer lane so the primary's fire-and-forget sidecar write cannot
            // land before the user switches. The replace-vs-skip decision must be made
            // against the settled file state on the lane — deciding from a snapshot taken
            // while the primary write was still queued was a TOCTOU: the switch saw no
            // file (replace=false), then the queued primary write landed, and the
            // switch's own write saw an existing file and skipped. The explicit
            // choice never reached disk.
            using (var lane = new ParkedWriterLane())
            {
                vm.SearchLyricsForTrack(track);
                await PumpUntilAsync(SearchSettled(vm, lrcLib, netEase));
                Assert.Equal("LRCLIB", vm.LyricsSourceName);
                Assert.True(vm.HasAlternateLyrics);
                Assert.False(File.Exists(lrcPath)); // primary write is queued, not landed

                vm.SwitchToAlternateLyricsCommand.Execute(null);
            }

            await PumpUntilAsync(() => SafeRead(lrcPath).Contains("alternate lyrics"));
            Assert.Contains("alternate lyrics", SafeRead(lrcPath));
            Assert.True(AppWrittenSidecarRegistry.Default.Contains(lrcPath));
            await PumpUntilAsync(() => track.SyncedLyrics == "[00:02.00]alternate lyrics");
            Assert.Equal("[00:02.00]alternate lyrics", track.SyncedLyrics);
        }
        finally { Cleanup(track.FilePath); }
    }

    [AvaloniaFact]
    public async Task RemoveLyrics_WithWritesStillQueued_DoesNotResurrectFiles()
    {
        var lrcLib = new StubLrcLib { GetImpl = _ => FromResult(Result(synced: "[00:01.00]doomed lyrics")) };
        var netEase = new StubNetEase();
        var (vm, _, track) = Mount(lrcLib, netEase);
        var lrcPath = Path.ChangeExtension(track.FilePath, ".lrc");
        var cacheLrc = Path.Combine(AppPaths.DataRoot, "lyrics_cache", $"{track.Id}.lrc");

        try
        {
            // Park the lane BEFORE the search: the cache and sidecar writes queue up
            // behind the blocker, exactly the "unawaited write still in flight" window.
            using (var lane = new ParkedWriterLane())
            {
                vm.SearchLyricsForTrack(track);
                await PumpUntilAsync(SearchSettled(vm, lrcLib, netEase));
                Assert.True(vm.CanRemoveLyrics);
                Assert.False(File.Exists(cacheLrc));

                // RemoveLyrics awaits the lane, so release it from the background.
                lane.ReleaseAfter(100);
                await vm.RemoveLyricsCommand.ExecuteAsync(null);
            }

            // The queued writes must have landed as no-ops (removal stamp) or been
            // deleted behind (lane FIFO) — either way nothing may resurrect.
            await Task.Delay(200);
            Dispatcher.UIThread.RunJobs();
            Assert.False(File.Exists(cacheLrc));
            Assert.False(File.Exists(lrcPath));
            Assert.False(AppWrittenSidecarRegistry.Default.Contains(lrcPath));
            Assert.Equal(string.Empty, track.Lyrics);
            Assert.Equal(string.Empty, track.SyncedLyrics);
        }
        finally
        {
            Cleanup(track.FilePath);
            try { if (File.Exists(cacheLrc)) File.Delete(cacheLrc); } catch { }
        }
    }

    [AvaloniaFact]
    public async Task RemoveLyrics_TrashFailure_KeepsRegistryEntrySoRetryStillWorks()
    {
        var lrcLib = new StubLrcLib { GetImpl = _ => FromResult(Result(synced: "[00:01.00]from the app")) };
        var netEase = new StubNetEase();
        var (vm, _, track) = Mount(lrcLib, netEase);
        var lrcPath = Path.ChangeExtension(track.FilePath, ".lrc");

        try
        {
            vm.SearchLyricsForTrack(track);
            await PumpUntilAsync(() => File.Exists(lrcPath)
                && AppWrittenSidecarRegistry.Default.Contains(lrcPath));

            // Trash fails (file locked / bin unavailable): the file stays on disk, so
            // the registry entry must stay too — dropping it first made the app's own
            // sidecar permanently look user-owned and forever un-removable.
            vm.TrashSidecarFile = _ => false;
            await vm.RemoveLyricsCommand.ExecuteAsync(null);

            Assert.True(File.Exists(lrcPath));
            Assert.True(AppWrittenSidecarRegistry.Default.Contains(lrcPath));

            // Retry with the lock gone: now it trashes and unregisters.
            vm.TrashSidecarFile = p => { File.Delete(p); return true; };
            await vm.RemoveLyricsCommand.ExecuteAsync(null);

            Assert.False(File.Exists(lrcPath));
            Assert.False(AppWrittenSidecarRegistry.Default.Contains(lrcPath));
        }
        finally { Cleanup(track.FilePath); }
    }

    // ── Stale-result generation guard ──

    [AvaloniaFact]
    public async Task StaleSearchResult_AfterTrackChange_IsDiscarded()
    {
        var blockedGet = new TaskCompletionSource<LrcLibResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lrcLib = new StubLrcLib
        {
            // Track A's /get hangs (slow network); track B misses instantly.
            GetImpl = artist => artist == "Artist A"
                ? blockedGet.Task
                : Task.FromResult<LrcLibResult?>(null),
        };
        var netEase = new StubNetEase();
        var (vm, player, trackA) = Mount(lrcLib, netEase, artist: "Artist A");

        vm.SearchLyricsForTrack(trackA);
        await PumpUntilAsync(() => lrcLib.GetCalls >= 1 && vm.IsSearching);
        Assert.True(vm.IsSearching);

        var trackB = new Track
        {
            Title = "Test Song",
            Artist = "Artist B",
            Duration = TimeSpan.FromSeconds(200),
            FilePath = TempTrackPath(),
        };
        player.CurrentTrack = trackB;
        vm.SearchLyricsForTrack(trackB);
        await PumpUntilAsync(() => vm.SearchFailedMessage == "No Lyrics found.");

        // Track A's search finally lands with lyrics — a stale generation that
        // must not overwrite track B's settled "No Lyrics found." state.
        blockedGet.SetResult(Result(synced: "[00:01.00]stale lyrics from track A", artist: "Artist A"));
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(vm.LyricLines);
        Assert.Equal("No Lyrics found.", vm.SearchFailedMessage);
        Assert.Equal(string.Empty, vm.LyricsSourceName);
        Assert.False(vm.IsSearching);
    }
}
