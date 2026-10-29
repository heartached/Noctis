using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #148: new files found by the folder watcher or a library scan are measured and
/// tagged for ReplayGain on their own, once their album has finished copying.
/// </summary>
public class ReplayGainAutoScanTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Quiet = ReplayGainAutoScanQueue.DefaultQuietPeriod;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "rg148-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private Track Song(string album, string file, string? dir = null, bool onDisk = false)
    {
        var path = Path.Combine(_root, dir ?? album, file);
        if (onDisk)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        }
        return new Track
        {
            FilePath = path,
            Title = file,
            Album = album,
            Artist = "Artist",
            AlbumId = Track.ComputeAlbumId("Artist", album),
            Duration = TimeSpan.FromMinutes(3),
            SourceType = SourceType.Local,
        };
    }

    // ── Settle / batching (ReplayGainAutoScanQueue) ──

    [Fact]
    public void Album_WaitsForTheQuietPeriod_ThenComesOutWhole()
    {
        var q = new ReplayGainAutoScanQueue();
        q.Add(new[] { Song("A", "1.flac"), Song("A", "2.flac") }, T0);

        Assert.Empty(q.TakeReady(T0 + Quiet - TimeSpan.FromSeconds(1), _ => true));

        var album = Assert.Single(q.TakeReady(T0 + Quiet, _ => true));
        Assert.Equal(2, album.Tracks.Count);
        Assert.True(q.IsEmpty);
    }

    [Fact]
    public void EachNewSong_RestartsItsAlbumsQuietPeriod()
    {
        var q = new ReplayGainAutoScanQueue();
        q.Add(new[] { Song("A", "1.flac") }, T0);
        q.Add(new[] { Song("A", "2.flac") }, T0 + TimeSpan.FromSeconds(20));

        Assert.Empty(q.TakeReady(T0 + Quiet + TimeSpan.FromSeconds(5), _ => true));
        var album = Assert.Single(q.TakeReady(T0 + TimeSpan.FromSeconds(20) + Quiet, _ => true));
        Assert.Equal(new[] { "1.flac", "2.flac" }, album.Tracks.Select(t => t.Title));
    }

    [Fact]
    public void SongsArrivingInTheSameFolder_HoldAnotherAlbumThere()
    {
        // A copy still going into the folder (per-disc album tags, a second album) counts
        // as "not finished" for every album already waiting in it.
        var q = new ReplayGainAutoScanQueue();
        q.Add(new[] { Song("A", "1.flac", dir: "Drop") }, T0);
        q.Add(new[] { Song("B", "1.flac", dir: "Drop") }, T0 + TimeSpan.FromSeconds(20));

        Assert.Empty(q.TakeReady(T0 + Quiet + TimeSpan.FromSeconds(5), _ => true));
        Assert.Equal(2, q.TakeReady(T0 + TimeSpan.FromSeconds(20) + Quiet, _ => true).Count);
    }

    [Fact]
    public void FilesStillChanging_HoldTheAlbum_UntilTheSettleCapRunsOut()
    {
        var q = new ReplayGainAutoScanQueue(maxSettleWait: TimeSpan.FromMinutes(5));
        q.Add(new[] { Song("A", "1.flac") }, T0);
        var asked = 0;

        Assert.Empty(q.TakeReady(T0 + Quiet, _ => { asked++; return false; }));
        Assert.Empty(q.TakeReady(T0 + Quiet + TimeSpan.FromMinutes(4), _ => false));
        Assert.Equal(1, asked);
        // A file that never settles can't hold its album forever.
        Assert.Single(q.TakeReady(T0 + Quiet + TimeSpan.FromMinutes(5), _ => false));
    }

    [Fact]
    public void SameFileTwice_ListedOnce()
    {
        var q = new ReplayGainAutoScanQueue();
        var song = Song("A", "1.flac");
        q.Add(new[] { song }, T0);
        q.Add(new[] { Song("A", "1.flac") }, T0 + TimeSpan.FromSeconds(1));

        Assert.Single(Assert.Single(q.TakeReady(T0 + TimeSpan.FromMinutes(1), _ => true)).Tracks);
    }

    [Fact]
    public void ManyReadyAlbums_ComeOutInBatches()
    {
        var q = new ReplayGainAutoScanQueue();
        q.Add(Enumerable.Range(0, 5).Select(i => Song("Album " + i, "1.flac")), T0);

        Assert.Equal(2, q.TakeReady(T0 + Quiet, _ => true, max: 2).Count);
        Assert.Equal(3, q.Count);
        Assert.Equal(3, q.TakeReady(T0 + Quiet, _ => true).Count);
    }

    // ── Album grouping ──

    [Fact]
    public void Songs_AreGroupedPerAlbum_AndUntitledSinglesStandAlone()
    {
        var q = new ReplayGainAutoScanQueue();
        var single1 = Song("", "x.mp3", dir: "Loose");
        var single2 = Song("", "y.mp3", dir: "Loose");
        single1.AlbumId = single2.AlbumId = Track.UnknownAlbumBucketId;
        q.Add(new[] { Song("A", "1.flac"), Song("B", "1.flac"), Song("A", "2.flac"), single1, single2 }, T0);

        var ready = q.TakeReady(T0 + Quiet, _ => true);

        Assert.Equal(4, ready.Count);
        Assert.Equal(2, ready.Single(a => a.AlbumName == "A").Tracks.Count);
        Assert.Single(ready.Single(a => a.AlbumName == "B").Tracks);
        // One "album" per untitled single: no shared album gain across unrelated songs.
        Assert.Equal(2, ready.Count(a => a.AlbumName == ""));
    }

    // ── The service ──

    private sealed class FakeScanner : IReplayGainScannerService
    {
        public List<(IReadOnlyList<Track> Tracks, ReplayGainScanOptions Options)> Runs { get; } = new();
        public bool IsAvailable => true;

        public Task<ScanSummary> ScanAsync(IReadOnlyList<Track> tracks, bool albumMode, IProgress<ScanProgress> progress, CancellationToken ct)
            => ScanAsync(tracks, albumMode, progress, ct, new ReplayGainScanOptions());

        public Task<ScanSummary> ScanAsync(IReadOnlyList<Track> tracks, bool albumMode, IProgress<ScanProgress> progress,
            CancellationToken ct, ReplayGainScanOptions options)
        {
            Assert.True(albumMode);
            Runs.Add((tracks, options));
            var written = tracks.Count(t => options.ShouldWrite?.Invoke(t) ?? true);
            return Task.FromResult(new ScanSummary { Scanned = written });
        }
    }

    private sealed class Rig
    {
        public FakeScanner Scanner { get; } = new();
        public FakeLibraryService Library { get; } = new();
        public AppSettings Settings { get; } = new() { AutoReplayGainScanEnabled = true };
        public DateTime Now { get; set; } = T0;
        public HashSet<string> Tagged { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Playing { get; set; }
        public int TagsWritten { get; private set; }
        public ReplayGainAutoScanService Service { get; }

        public Rig()
        {
            Service = new ReplayGainAutoScanService(
                Scanner, Library, () => Settings,
                playingPath: () => Playing,
                onTagsWritten: () => TagsWritten++,
                clock: () => Now,
                tickInterval: TimeSpan.FromHours(1), // ticks are driven by the test
                hasReplayGainTags: Tagged.Contains,
                isSettled: _ => true);
        }

        public Task TickAfter(TimeSpan elapsed)
        {
            Now += elapsed;
            return Service.TickAsync();
        }
    }

    [Fact]
    public async Task SettingOff_NothingIsQueuedOrScanned()
    {
        var rig = new Rig();
        rig.Settings.AutoReplayGainScanEnabled = false;

        rig.Service.Enqueue(new[] { Song("A", "1.flac", onDisk: true) });
        await rig.TickAfter(TimeSpan.FromMinutes(10));

        Assert.Equal(0, rig.Service.PendingAlbumCount);
        Assert.Empty(rig.Scanner.Runs);
    }

    [Fact]
    public async Task SettingTurnedOffWhileWaiting_DropsTheQueue()
    {
        var rig = new Rig();
        rig.Service.Enqueue(new[] { Song("A", "1.flac", onDisk: true) });
        Assert.Equal(1, rig.Service.PendingAlbumCount);

        rig.Settings.AutoReplayGainScanEnabled = false;
        await rig.TickAfter(TimeSpan.FromMinutes(10));

        Assert.Equal(0, rig.Service.PendingAlbumCount);
        Assert.Empty(rig.Scanner.Runs);
    }

    [Fact]
    public async Task NewAlbum_IsScannedOnceQuiet_AndEverySongIsWritten()
    {
        var rig = new Rig();
        var songs = new[] { Song("A", "1.flac", onDisk: true), Song("A", "2.flac", onDisk: true) };
        rig.Library.TrackList.AddRange(songs);
        rig.Service.Enqueue(songs);

        await rig.TickAfter(Quiet - TimeSpan.FromSeconds(1));
        Assert.Empty(rig.Scanner.Runs);

        await rig.TickAfter(TimeSpan.FromSeconds(1));
        var run = Assert.Single(rig.Scanner.Runs);
        Assert.Equal(2, run.Tracks.Count);
        Assert.True(run.Options.Background);
        Assert.All(run.Tracks, t => Assert.True(run.Options.ShouldWrite!(t)));
        Assert.Equal(1, rig.TagsWritten);
        Assert.Equal(0, rig.Service.PendingAlbumCount);
    }

    [Fact]
    public async Task FilesAlreadyTagged_AreNotRewritten()
    {
        var rig = new Rig();
        var tagged = Song("A", "1.flac", onDisk: true);
        var fresh = Song("A", "2.flac", onDisk: true);
        rig.Tagged.Add(tagged.FilePath);
        rig.Library.TrackList.AddRange(new[] { tagged, fresh });
        rig.Service.Enqueue(new[] { tagged, fresh });

        await rig.TickAfter(Quiet);

        var run = Assert.Single(rig.Scanner.Runs);
        // Still measured: it is part of the album's gain.
        Assert.Contains(tagged, run.Tracks);
        Assert.False(run.Options.ShouldWrite!(tagged));
        Assert.True(run.Options.ShouldWrite!(fresh));
    }

    [Fact]
    public async Task AlbumWhoseNewFilesAreAllTagged_IsNotScannedAtAll()
    {
        var rig = new Rig();
        var song = Song("A", "1.flac", onDisk: true);
        rig.Tagged.Add(song.FilePath);
        rig.Service.Enqueue(new[] { song });

        await rig.TickAfter(Quiet);

        Assert.Empty(rig.Scanner.Runs);
        Assert.Equal(0, rig.TagsWritten);
    }

    [Fact]
    public async Task OneNewSongInAKnownAlbum_AlbumGainCoversTheWholeAlbum_OldFilesUntouched()
    {
        var rig = new Rig();
        var old1 = Song("A", "1.flac", onDisk: true);
        var old2 = Song("A", "2.flac", onDisk: true);
        var other = Song("B", "1.flac", onDisk: true);
        var bonus = Song("A", "3 (bonus).flac", onDisk: true);
        rig.Library.TrackList.AddRange(new[] { old1, old2, other, bonus });
        rig.Service.Enqueue(new[] { bonus });

        await rig.TickAfter(Quiet);

        var run = Assert.Single(rig.Scanner.Runs);
        Assert.Equal(new[] { bonus, old1, old2 }.Select(t => t.FilePath).OrderBy(p => p),
                     run.Tracks.Select(t => t.FilePath).OrderBy(p => p));
        Assert.True(run.Options.ShouldWrite!(bonus));
        Assert.False(run.Options.ShouldWrite!(old1));
        Assert.False(run.Options.ShouldWrite!(old2));
    }

    [Fact]
    public async Task UntitledSingle_IsItsOwnAlbum_NotMixedWithOtherLooseSongs()
    {
        var rig = new Rig();
        var loose = Song("", "old.mp3", dir: "Loose", onDisk: true);
        var single = Song("", "new.mp3", dir: "Loose", onDisk: true);
        loose.AlbumId = single.AlbumId = Track.UnknownAlbumBucketId;
        rig.Library.TrackList.AddRange(new[] { loose, single });
        rig.Service.Enqueue(new[] { single });

        await rig.TickAfter(Quiet);

        Assert.Equal(new[] { single }, Assert.Single(rig.Scanner.Runs).Tracks);
    }

    [Fact]
    public async Task AlbumHoldingThePlayingSong_WaitsUntilItIsNotPlaying()
    {
        var rig = new Rig();
        var songs = new[] { Song("A", "1.flac", onDisk: true), Song("A", "2.flac", onDisk: true) };
        rig.Service.Enqueue(songs);
        rig.Playing = songs[1].FilePath;

        await rig.TickAfter(Quiet);
        Assert.Empty(rig.Scanner.Runs);
        Assert.Equal(1, rig.Service.PendingAlbumCount);

        rig.Playing = Song("B", "x.flac").FilePath;
        await rig.TickAfter(Quiet);
        Assert.Equal(2, Assert.Single(rig.Scanner.Runs).Tracks.Count);
    }

    [Fact]
    public async Task DeletedBeforeTheScan_IsSkipped()
    {
        var rig = new Rig();
        var gone = Song("A", "1.flac", onDisk: true);
        var kept = Song("A", "2.flac", onDisk: true);
        rig.Service.Enqueue(new[] { gone, kept });
        File.Delete(gone.FilePath);

        await rig.TickAfter(Quiet);

        Assert.Equal(new[] { kept }, Assert.Single(rig.Scanner.Runs).Tracks);
    }

    [Fact]
    public void AppsOwnTempCopies_AreIgnored()
    {
        var rig = new Rig();
        rig.Service.Enqueue(new[] { Song("A", ".noctis-0123abcd.flac") });
        Assert.Equal(0, rig.Service.PendingAlbumCount);
    }

    /// <summary>Real ffmpeg (skipped without one): a measured-only song counts toward the
    /// album gain but its file is not written.</summary>
    [Fact]
    public async Task Scanner_MeasuredOnlySong_CountsForAlbumGain_ButIsNotWritten()
    {
        var converter = new AudioConverterService(() => string.Empty, new MetadataService());
        var ffmpeg = converter.GetFfmpegPath();
        if (ffmpeg == null) return; // no ffmpeg on this machine

        Directory.CreateDirectory(_root);
        string Tone(string name, double volume)
        {
            var path = Path.Combine(_root, name);
            var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi",
                         "-i", "sine=f=440:d=8", "-af", $"volume={volume.ToString(System.Globalization.CultureInfo.InvariantCulture)}", path })
                psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
            return path;
        }
        var oldSong = Song("A", "old.flac");
        oldSong.FilePath = Tone("old.flac", 1.0);
        var newSong = Song("A", "new.flac");
        newSong.FilePath = Tone("new.flac", 0.1); // 20 dB quieter

        var summary = await new ReplayGainScannerService(converter).ScanAsync(
            new[] { oldSong, newSong }, albumMode: true, new Progress<ScanProgress>(_ => { }), CancellationToken.None,
            new ReplayGainScanOptions { ShouldWrite = t => ReferenceEquals(t, newSong), Background = true });

        Assert.Equal(1, summary.Scanned);
        Assert.Equal(0, summary.Failed);
        Assert.True(string.IsNullOrEmpty(AdvancedTagIO.ReadAll(oldSong.FilePath).ReplayGainTrackGain));
        var written = AdvancedTagIO.ReadAll(newSong.FilePath);
        double Db(string v) => double.Parse(v.Replace(" dB", ""), System.Globalization.CultureInfo.InvariantCulture);
        var trackGain = Db(written.ReplayGainTrackGain);
        var albumGain = Db(written.ReplayGainAlbumGain);
        // The album is dominated by the loud old song, so its gain is far below the quiet
        // new song's own track gain.
        Assert.True(trackGain - albumGain > 10, $"track {trackGain} dB, album {albumGain} dB");
    }

    [Fact]
    public async Task LibraryTracksAdded_FeedsTheQueue()
    {
        var rig = new Rig();
        var lib = new FakeLibraryService();
        var service = new ReplayGainAutoScanService(rig.Scanner, lib, () => rig.Settings,
            clock: () => rig.Now, tickInterval: TimeSpan.FromHours(1), hasReplayGainTags: _ => false, isSettled: _ => true);
        service.Attach();

        lib.RaiseTracksAdded(new[] { Song("A", "1.flac", onDisk: true) });
        Assert.Equal(1, service.PendingAlbumCount);

        rig.Now += Quiet;
        await service.TickAsync();
        Assert.Single(rig.Scanner.Runs);
        service.Dispose();
    }
}
