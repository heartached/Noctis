using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Edit Info (Discord 11-23): "Editing metadata can leave us stuck on the metadata page because
/// we can't close the window until the changes have been saved. Sometimes it takes ages to apply
/// even very simple changes", "The Cancel button sometimes doesn't actually cancel anything and
/// still applies the changes; it just closes the page", "at least let us click outside and have
/// the changes saved in the background". Save is the commit: the window closes at once and the
/// files are written behind it; Cancel never applies anything.
/// </summary>
public class MetadataBackgroundSaveTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "bgsave-" + Guid.NewGuid().ToString("N"));

    public MetadataBackgroundSaveTests(ITestOutputHelper o)
    {
        _o = o;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── Cancel ──

    [Fact]
    public async Task Cancel_AfterEdits_LeavesTrackAndFileUnchanged()
    {
        var track = RealTrack("song", "Old Title");
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        using var p = new TestPersistenceService();
        var vm = new MetadataViewModel(track, new MetadataService(), lib, p, new FakeAnimatedCoverService());
        await vm.InitializeAsync();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.Title = "New Title";
        vm.Genre = "Shoegaze";
        vm.Rating = 5;
        vm.VolumeAdjust = 40;
        vm.Lyrics = "a line";
        vm.CancelCommand.Execute(null);

        Assert.True(closed);
        Assert.Equal("Old Title", track.Title);
        Assert.Equal("Pop", track.Genre);
        Assert.Equal(0, track.Rating);
        Assert.Equal(0, track.VolumeAdjust);
        Assert.True(string.IsNullOrEmpty(track.Lyrics));
        using var f = TagLib.File.Create(track.FilePath);
        Assert.Equal("Old Title", f.Tag.Title);
        Assert.False(File.Exists(Path.ChangeExtension(track.FilePath, ".txt")));
    }

    [Fact]
    public async Task Cancel_IsDisabledOnceSaveIsPressed()
    {
        var track = RealTrack("song", "Old Title");
        var meta = new GatedMetadata();
        var vm = NewVm(track, meta, out _);
        await vm.InitializeAsync();

        vm.Title = "New Title";
        var save = vm.SaveCommand.ExecuteAsync(null);
        Assert.True(meta.WaitEntered(1));

        Assert.False(vm.CancelCommand.CanExecute(null));
        meta.Release();
        await save;
    }

    // ── Save closes at once, the files follow ──

    [Fact]
    public async Task Save_ClosesTheWindowBeforeTheFilesAreWritten_AndTheFilesEndUpCorrect()
    {
        var track = RealTrack("song", "Old Title");
        var meta = new GatedMetadata();
        var vm = NewVm(track, meta, out _);
        await vm.InitializeAsync();
        var closed = 0;
        vm.CloseRequested += (_, _) => Interlocked.Exchange(ref closed, 1);

        vm.Title = "New Title";
        var save = vm.SaveCommand.ExecuteAsync(null);

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref closed) == 1, 2000), "the window waited for the file write");
        Assert.False(save.IsCompleted);
        // The library shows the edit at once.
        Assert.Equal("New Title", track.Title);

        meta.Release();
        await save;
        Assert.Equal(string.Empty, vm.SaveErrorMessage);
        using var f = TagLib.File.Create(track.FilePath);
        Assert.Equal("New Title", f.Tag.Title);
    }

    [Fact]
    public async Task Reopen_WhileSaving_ShowsTheNewValues_AndLoadsOnlyOnceTheSaveHasLanded()
    {
        var track = RealTrack("song", "Old Title");
        var meta = new GatedMetadata();
        var vm = NewVm(track, meta, out var lib);
        await vm.InitializeAsync();

        vm.Title = "New Title";
        vm.Genre = "Shoegaze";
        var save = vm.SaveCommand.ExecuteAsync(null);
        Assert.True(meta.WaitEntered(1));

        using var p2 = new TestPersistenceService();
        var again = new MetadataViewModel(track, meta, lib, p2, new FakeAnimatedCoverService());
        Assert.Equal("New Title", again.Title);
        Assert.Equal("Shoegaze", again.Genre);
        var load = again.InitializeAsync();
        await Task.Delay(150);
        Assert.False(load.IsCompleted, "the editor read the file while its save was still writing it");
        Assert.True(again.IsLoading);

        meta.Release();
        await save;
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("New Title", again.Title);
        Assert.Equal("Shoegaze", again.Genre);
    }

    [Fact]
    public async Task TwoSavesInARow_OnTheSameSong_WriteInOrder()
    {
        var track = RealTrack("song", "Old Title");
        var meta = new GatedMetadata();
        var first = NewVm(track, meta, out var lib);
        await first.InitializeAsync();
        first.Title = "First";
        var save1 = first.SaveCommand.ExecuteAsync(null);
        Assert.True(meta.WaitEntered(1));

        using var p2 = new TestPersistenceService();
        var second = new MetadataViewModel(track, meta, lib, p2, new FakeAnimatedCoverService());
        var load = second.InitializeAsync();
        second.Title = "Second"; // typed while it loads: kept
        meta.Release();
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        await second.SaveCommand.ExecuteAsync(null);
        await save1;

        Assert.Equal(1, meta.MaxConcurrentOnOnePath);
        Assert.Equal("Second", track.Title);
        using var f = TagLib.File.Create(track.FilePath);
        Assert.Equal("Second", f.Tag.Title);
    }

    // ── Speed ──

    [Fact]
    public async Task AlbumSave_WritesSeveralFilesAtOnce_ButNotAllOfThem()
    {
        var tracks = Enumerable.Range(1, 8).Select(i => RealTrack($"t{i}", $"Track {i}", album: "LP")).ToList();
        var meta = new GatedMetadata { HoldMs = 120 };
        meta.Release();
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        using var p = new TestPersistenceService();
        var vm = new MetadataViewModel(tracks[0], meta, lib, p, new FakeAnimatedCoverService(),
            albumScoped: true, albumTracks: tracks.ToList());
        await vm.InitializeAsync();

        vm.Album = "LP (Deluxe)";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.InRange(meta.MaxConcurrent, 2, 4);
        Assert.All(tracks, t =>
        {
            using var f = TagLib.File.Create(t.FilePath);
            Assert.Equal("LP (Deluxe)", f.Tag.Album);
        });
    }

    [Fact]
    public async Task ArtworkSave_SkipsTracksThatAlreadyCarryThatCover()
    {
        var tracks = Enumerable.Range(1, 3).Select(i => RealTrack($"t{i}", $"Track {i}", album: "LP")).ToList();
        var art = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        tracks[0].ArtworkHash = TrackArtwork.Fingerprint(art);
        tracks[1].ArtworkHash = "OLD";
        var meta = new GatedMetadata();
        meta.Release();
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        using var p = new TestPersistenceService();
        var vm = new MetadataViewModel(tracks[0], meta, lib, p, new FakeAnimatedCoverService(),
            albumScoped: true, albumTracks: tracks.ToList());
        await vm.InitializeAsync();

        SetField(vm, "_newArtworkData", art);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.DoesNotContain(tracks[0].FilePath, meta.ArtPaths);
        Assert.Contains(tracks[1].FilePath, meta.ArtPaths);
        Assert.Contains(tracks[2].FilePath, meta.ArtPaths);
        Assert.All(tracks, t => Assert.Equal(TrackArtwork.Fingerprint(art), t.ArtworkHash));
    }

    // ── No file work on the UI thread ──

    [Fact]
    public async Task ArtworkCacheWrite_RunsOffTheUiContext()
    {
        var tracks = Enumerable.Range(1, 2).Select(i => RealTrack($"t{i}", $"Track {i}", album: "LP")).ToList();
        var meta = new GatedMetadata();
        meta.Release();
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var ui = new UiContext();
        using var p = new RecordingPersistence(ui);
        var vm = new MetadataViewModel(tracks[0], meta, lib, p, new FakeAnimatedCoverService(),
            albumScoped: true, albumTracks: tracks.ToList());
        await vm.InitializeAsync();
        SetField(vm, "_newArtworkData", new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 9 });

        var done = new TaskCompletionSource();
        ui.Post(async _ =>
        {
            try { await vm.SaveCommand.ExecuteAsync(null); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        }, null);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(p.ArtworkSaves > 0);
        Assert.Equal(0, p.ArtworkSavesOnUi);
    }

    // ── Before/after timing (owner 11-23). Opt-in: NOCTIS_METADATA_BENCH=1 ──

    [Fact]
    public async Task Benchmark_TwentyTrackAlbumEdit()
    {
        if (Environment.GetEnvironmentVariable("NOCTIS_METADATA_BENCH") != "1") return;
        const int count = 20;
        const int sizeMb = 25;
        var tracks = new List<Track>();
        for (var i = 1; i <= count; i++)
        {
            var t = RealTrack($"b{i:00}", $"Track {i}", album: "Bench");
            using (var fs = new FileStream(t.FilePath, FileMode.Append))
                fs.Write(new byte[sizeMb * 1024 * 1024]);
            tracks.Add(t);
        }
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        using var p = new TestPersistenceService();
        var vm = new MetadataViewModel(tracks[0], new MetadataService(), lib, p, new FakeAnimatedCoverService(),
            albumScoped: true, albumTracks: tracks.ToList());
        await vm.InitializeAsync();

        var art = new byte[300 * 1024];
        new Random(7).NextBytes(art);
        art[0] = 0xFF; art[1] = 0xD8; art[2] = 0xFF;
        vm.Album = "Bench (Remastered)";
        vm.Genre = "Ambient";
        SetField(vm, "_newArtworkData", art);

        var sw = Stopwatch.StartNew();
        long closedAt = -1;
        vm.CloseRequested += (_, _) => closedAt = sw.ElapsedMilliseconds;
        await vm.SaveCommand.ExecuteAsync(null);
        var total = sw.ElapsedMilliseconds;
        var line = $"BENCH {count} x {sizeMb} MB FLAC, album+genre+cover: window closed after {closedAt} ms, save done after {total} ms";
        _o.WriteLine(line);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "noctis-metadata-bench.txt"), line + Environment.NewLine);

        Assert.All(tracks, t =>
        {
            using var f = TagLib.File.Create(t.FilePath);
            Assert.Equal("Bench (Remastered)", f.Tag.Album);
        });
    }

    // ── Helpers ──

    private Track RealTrack(string name, string title, string album = "B")
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        var path = ReplayGainWriteTests.CreateFlac(dir);
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Title = title;
            f.Tag.Album = album;
            f.Tag.Performers = new[] { "A" };
            f.Tag.AlbumArtists = new[] { "A" };
            f.Tag.Genres = new[] { "Pop" };
            f.Save();
        }
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = title, Artist = "A", AlbumArtist = "A", Album = album,
            TrackNumber = 1, Genre = "Pop", FilePath = path,
        };
        track.AlbumId = Track.ComputeAlbumId(track.AlbumArtist, track.Album);
        return track;
    }

    private static MetadataViewModel NewVm(Track track, IMetadataService meta, out FakeLibraryService lib)
    {
        lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        return new MetadataViewModel(track, meta, lib, new TestPersistenceService(), new FakeAnimatedCoverService());
    }

    private static void SetField(MetadataViewModel vm, string name, object value)
    {
        var field = typeof(MetadataViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(vm, value);
    }

    /// <summary>The real metadata service, with every tag and cover write held at a gate until
    /// <see cref="Release"/>, counting how many run at once (overall and on one file).</summary>
    private sealed class GatedMetadata : IMetadataService
    {
        private readonly MetadataService _inner = new();
        private readonly ManualResetEventSlim _gate = new(false);
        private readonly ConcurrentDictionary<string, int> _perPath = new(StringComparer.OrdinalIgnoreCase);
        private int _entered, _running, _maxRunning, _maxPerPath;
        public int HoldMs { get; init; }
        public ConcurrentBag<string> ArtPaths { get; } = new();
        public int MaxConcurrent => Volatile.Read(ref _maxRunning);
        public int MaxConcurrentOnOnePath => Volatile.Read(ref _maxPerPath);

        public void Release() => _gate.Set();
        public bool WaitEntered(int n) => SpinWait.SpinUntil(() => Volatile.Read(ref _entered) >= n, 5000);

        private bool Gated(string path, Func<bool> write)
        {
            Interlocked.Increment(ref _entered);
            var running = Interlocked.Increment(ref _running);
            InterlockedMax(ref _maxRunning, running);
            var onPath = _perPath.AddOrUpdate(path, 1, (_, v) => v + 1);
            InterlockedMax(ref _maxPerPath, onPath);
            try
            {
                _gate.Wait(TimeSpan.FromSeconds(10));
                if (HoldMs > 0) Thread.Sleep(HoldMs);
                return write();
            }
            finally
            {
                _perPath.AddOrUpdate(path, 0, (_, v) => v - 1);
                Interlocked.Decrement(ref _running);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value
                   && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }

        public Track? ReadTrackMetadata(string filePath) => _inner.ReadTrackMetadata(filePath);
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) => _inner.ReadTrackMetadata(filePath, out embeddedArt);
        public byte[]? ExtractAlbumArt(string filePath) => _inner.ExtractAlbumArt(filePath);
        public bool WriteTrackMetadata(Track track) => Gated(track.FilePath, () => _inner.WriteTrackMetadata(track));
        public bool WriteTrackMetadata(Track track, bool genreEdited) => Gated(track.FilePath, () => _inner.WriteTrackMetadata(track, genreEdited));
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null)
            => Gated(targetFilePath, () => _inner.WriteTrackMetadata(track, targetFilePath, titleOverride));
        public bool WriteAlbumArt(string filePath, byte[]? imageData)
        {
            ArtPaths.Add(filePath);
            return Gated(filePath, () => _inner.WriteAlbumArt(filePath, imageData));
        }
        public bool ClearYear(string filePath) => _inner.ClearYear(filePath);
        public bool ClearVersion(string filePath) => _inner.ClearVersion(filePath);
        public bool WriteLyrics(string filePath, string? plain, string? synced, Func<EmbeddedLyricsText, bool>? onlyIf = null)
            => _inner.WriteLyrics(filePath, plain, synced, onlyIf);
        public bool WriteRating(string filePath, int rating, bool isDisliked) => _inner.WriteRating(filePath, rating, isDisliked);
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields, AdvancedTagIO.AdvancedFields original)
            => ((IMetadataService)_inner).WriteAdvancedFields(filePath, fields, original);
        public AudioFileInfo? ReadFileInfo(string filePath) => _inner.ReadFileInfo(filePath);
    }

    /// <summary>Counts cover-cache writes, and those made on the UI-like context.</summary>
    private sealed class RecordingPersistence(SynchronizationContext ui) : TestPersistenceService
    {
        private int _saves, _onUi;
        public int ArtworkSaves => Volatile.Read(ref _saves);
        public int ArtworkSavesOnUi => Volatile.Read(ref _onUi);

        public override void SaveArtwork(Guid albumId, byte[] imageData)
        {
            Interlocked.Increment(ref _saves);
            if (ReferenceEquals(SynchronizationContext.Current, ui)) Interlocked.Increment(ref _onUi);
        }
    }

    /// <summary>Runs posted work on the pool with itself installed as the current context.</summary>
    private sealed class UiContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SetSynchronizationContext(this);
                try { d(state); }
                finally { SetSynchronizationContext(null); }
            });
    }
}
