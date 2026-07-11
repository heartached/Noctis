using System.Reflection;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Regression tests for the album/track Metadata editor (MetadataViewModel).
/// These cover the album-scoped fan-out fixes: Work &amp; Movement, play count,
/// Options change-detection (no per-track clobber), and album-wide artwork.
/// The VM is driven with lightweight service fakes; Save() mutates the Track
/// models in place, which is what we assert on.
/// </summary>
public class MetadataViewModelTests
{
    // ── Album scope: Work & Movement ──

    [Fact]
    public async Task AlbumScope_WorkAndMovement_FansOutToAllTracks_WhenChanged()
    {
        var tracks = Album("Symphony", "Composer", 3);
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);

        vm.UseWorkAndMovement = true;
        vm.WorkName = "Symphony No. 5";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.All(tracks, t =>
        {
            Assert.True(t.UseWorkAndMovement);
            Assert.Equal("Symphony No. 5", t.WorkName);
        });
    }

    [Fact]
    public async Task AlbumScope_WorkName_Untouched_PreservesPerTrackValues()
    {
        var tracks = Album("Symphony", "Composer", 2);
        tracks[0].WorkName = "Work A";
        tracks[1].WorkName = "Work B";

        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);

        // Mixed WorkName loads blank; never touch it. Change an unrelated field.
        vm.Comment = "edited";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Work A", tracks[0].WorkName);
        Assert.Equal("Work B", tracks[1].WorkName);
    }

    // ── Album scope: play count ──

    [Fact]
    public async Task AlbumScope_ResetPlayCount_IsStagedUntilSave()
    {
        var tracks = Album("A", "X", 3);
        tracks[0].PlayCount = 5; tracks[0].LastPlayed = DateTime.UtcNow;
        tracks[1].PlayCount = 3; tracks[1].LastPlayed = DateTime.UtcNow;
        tracks[2].PlayCount = 8; tracks[2].LastPlayed = DateTime.UtcNow;

        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);

        vm.ResetPlayCountCommand.Execute(null);

        // Staged only: the display shows zero, but nothing touches the Track
        // models yet — Cancel must discard the reset.
        Assert.Equal("0", vm.PlayCountDisplay);
        Assert.All(tracks, t =>
        {
            Assert.NotEqual(0, t.PlayCount);
            Assert.NotNull(t.LastPlayed);
        });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.All(tracks, t =>
        {
            Assert.Equal(0, t.PlayCount);
            Assert.Null(t.LastPlayed);
        });
    }

    [Fact]
    public void AlbumScope_PlayCountDisplay_ShowsAlbumTotal()
    {
        var tracks = Album("A", "X", 3);
        tracks[0].PlayCount = 5;
        tracks[1].PlayCount = 3;
        tracks[2].PlayCount = 8;

        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);

        Assert.Equal("16", vm.PlayCountDisplay);
    }

    // ── Album scope: Options change-detection (the data-loss fix) ──

    [Fact]
    public async Task AlbumScope_UnchangedOptions_DoNotClobberPerTrackValues()
    {
        var tracks = Album("A", "X", 3);
        tracks[0].VolumeAdjust = 0;   tracks[0].EqPreset = "Rock";
        tracks[1].VolumeAdjust = -20; tracks[1].EqPreset = "Jazz";
        tracks[2].VolumeAdjust = 10;  tracks[2].EqPreset = string.Empty;

        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);

        // Change only a Details field; never open/touch Options.
        vm.Comment = "edited";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(0, tracks[0].VolumeAdjust);   Assert.Equal("Rock", tracks[0].EqPreset);
        Assert.Equal(-20, tracks[1].VolumeAdjust); Assert.Equal("Jazz", tracks[1].EqPreset);
        Assert.Equal(10, tracks[2].VolumeAdjust);  Assert.Equal(string.Empty, tracks[2].EqPreset);
    }

    [Fact]
    public async Task AlbumScope_ChangedVolume_FansOutToAllTracks()
    {
        var tracks = Album("A", "X", 2);
        tracks[0].VolumeAdjust = 0;
        tracks[1].VolumeAdjust = -20;

        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out _, out _);

        vm.VolumeAdjust = 50;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.All(tracks, t => Assert.Equal(50, t.VolumeAdjust));
    }

    // ── Artwork: album-wide on both album and single-track edits ──

    [Fact]
    public async Task AlbumScope_AddArtwork_WritesEveryTrack()
    {
        var tracks = Album("A", "X", 3);
        using var p = new TestPersistenceService();
        var vm = NewAlbumVm(tracks, p, out var meta, out _);

        SetNewArtwork(vm, new byte[] { 1, 2, 3 });
        await vm.SaveCommand.ExecuteAsync(null);

        foreach (var t in tracks)
            Assert.Contains(t.FilePath, meta.WrittenArtPaths);
    }

    [Fact]
    public async Task TrackScope_AddArtwork_WritesAllAlbumTracks()
    {
        // Single-track edit: artwork should still apply to the whole album,
        // resolved from the library (mirrors the Remove path).
        var album = Album("A", "X", 3);
        using var p = new TestPersistenceService();
        var meta = new FakeMetadataService();
        var library = new FakeLibraryService { TrackList = album.ToList() };
        var vm = new MetadataViewModel(album[0], meta, library, p, new FakeAnimatedCoverService(),
            albumScoped: false, albumTracks: null);

        SetNewArtwork(vm, new byte[] { 9, 9 });
        await vm.SaveCommand.ExecuteAsync(null);

        foreach (var t in album)
            Assert.Contains(t.FilePath, meta.WrittenArtPaths);
    }

    // ── Genre: off-list values (from a metadata search) must be displayable ──

    [Fact]
    public void Genre_SetToValueOutsideBuiltInList_IsAddedToGenreOptions()
    {
        // A genre applied from an online search (e.g. Deezer's "Rap/Hip Hop") isn't in the
        // built-in list. The ComboBox can only display a value present in GenreOptions, so the
        // VM must add it — otherwise the box shows its placeholder ("Mixed") and the applied
        // genre looks like it never took.
        var album = Album("A", "X", 1);
        using var p = new TestPersistenceService();
        var meta = new FakeMetadataService();
        var library = new FakeLibraryService { TrackList = album.ToList() };
        var vm = new MetadataViewModel(album[0], meta, library, p, new FakeAnimatedCoverService(),
            albumScoped: false, albumTracks: null);

        Assert.DoesNotContain("Rap/Hip Hop", vm.GenreOptions);

        vm.Genre = "Rap/Hip Hop";

        Assert.Contains("Rap/Hip Hop", vm.GenreOptions);
        Assert.Equal("Rap/Hip Hop", vm.Genre);
    }

    // ── Options: start/stop time parsing ──

    [Fact]
    public async Task TrackScope_StartTime_BareNumberParsesAsSeconds()
    {
        // "45" must mean 45 seconds — the TimeSpan.TryParse fallback reads it as 45 days.
        var album = Album("A", "X", 1);
        using var p = new TestPersistenceService();
        var vm = new MetadataViewModel(album[0], new FakeMetadataService(),
            new FakeLibraryService { TrackList = album.ToList() }, p, new FakeAnimatedCoverService(),
            albumScoped: false, albumTracks: null);

        vm.HasStartTime = true;
        vm.StartTime = "45";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(45_000, album[0].StartTimeMs);
    }

    // ── Timestamp Lyrics: first manual timing pass must survive Save ──

    [Fact]
    public async Task TimestampEdit_OnPlainOnlyTrack_SavesSyncedLyrics()
    {
        var album = Album("A", "X", 1);
        album[0].Lyrics = "line one\nline two";

        using var p = new TestPersistenceService();
        var vm = new MetadataViewModel(album[0], new FakeMetadataService(),
            new FakeLibraryService { TrackList = album.ToList() }, p, new FakeAnimatedCoverService(),
            albumScoped: false, albumTracks: null);

        // Lines seeded from the plain lyrics; no synced lyrics exist yet.
        Assert.Equal(2, vm.SyncedLyricLines.Count);
        Assert.False(vm.HasCustomSyncedLyrics);

        vm.SyncedLyricLines[0].TimestampText = "0:10.00";

        Assert.True(vm.HasCustomSyncedLyrics);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("[00:10.00]line one", album[0].SyncedLyrics);
    }

    // ── Multi-select rename: lyric sidecars follow the audio file ──

    [Fact]
    public async Task MultiSelectRename_MovesLyricSidecarsWithFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-md-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var tracks = Album("A", "X", 2);
            tracks[0].FilePath = Path.Combine(dir, "a.flac");
            tracks[1].FilePath = Path.Combine(dir, "b.flac");
            File.WriteAllText(tracks[0].FilePath, "audio");
            File.WriteAllText(tracks[1].FilePath, "audio");
            File.WriteAllText(Path.Combine(dir, "a.lrc"), "[00:01.00] hi");

            using var p = new TestPersistenceService();
            var vm = new MetadataViewModel(tracks[0], new FakeMetadataService(),
                new FakeLibraryService { TrackList = tracks.ToList() }, p, new FakeAnimatedCoverService(),
                albumScoped: true, albumTracks: tracks.ToList(), multiSelect: true);

            vm.ApplyRename = true; // default pattern: "%tracknumber2% - %title%"
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(Path.Combine(dir, "01 - Track 1.flac"), tracks[0].FilePath);
            Assert.True(File.Exists(Path.Combine(dir, "01 - Track 1.lrc")));
            Assert.False(File.Exists(Path.Combine(dir, "a.lrc")));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── Helpers ──

    private static List<Track> Album(string album, string albumArtist, int count)
    {
        var list = new List<Track>();
        for (int i = 1; i <= count; i++)
        {
            var t = new Track
            {
                Title = $"Track {i}",
                Album = album,
                AlbumArtist = albumArtist,
                Artist = albumArtist,
                TrackNumber = i,
                FilePath = Path.Combine(Path.GetTempPath(), "noctistest", album, $"track{i}.flac"),
            };
            t.AlbumId = Track.ComputeAlbumId(t.AlbumArtist, t.Album);
            list.Add(t);
        }
        return list;
    }

    private static MetadataViewModel NewAlbumVm(
        List<Track> tracks, IPersistenceService persistence,
        out FakeMetadataService meta, out FakeLibraryService library)
    {
        meta = new FakeMetadataService();
        library = new FakeLibraryService { TrackList = tracks.ToList() };
        return new MetadataViewModel(tracks[0], meta, library, persistence, new FakeAnimatedCoverService(),
            albumScoped: true, albumTracks: tracks.ToList());
    }

    private static void SetNewArtwork(MetadataViewModel vm, byte[] data)
    {
        var field = typeof(MetadataViewModel).GetField("_newArtworkData",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(vm, data);
    }

    // ── Service fakes ──

    private sealed class FakeMetadataService : IMetadataService
    {
        private readonly object _gate = new();
        public List<string> WrittenArtPaths { get; } = new();

        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt)
        {
            embeddedArt = null;
            return null;
        }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => true;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => true;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => true;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;

        public bool WriteAlbumArt(string filePath, byte[]? imageData)
        {
            lock (_gate) WrittenArtPaths.Add(filePath);
            return true;
        }
    }

    private sealed class FakeAnimatedCoverService : IAnimatedCoverService
    {
        public string? Resolve(Track track) => null;
        public Task<string> ImportAsync(Track track, string sourcePath, AnimatedCoverScope scope)
            => Task.FromResult(string.Empty);
        public void Remove(Track track, AnimatedCoverScope scope) { }
    }

    private sealed class FakeLibraryService : ILibraryService
    {
        public List<Track> TrackList { get; set; } = new();
        public IReadOnlyList<Track> Tracks => TrackList;
        public IReadOnlyList<Album> Albums => Array.Empty<Album>();
        public IReadOnlyList<Artist> Artists => Array.Empty<Artist>();

        public event EventHandler? LibraryUpdated { add { } remove { } }
        public event EventHandler<int>? ScanProgress { add { } remove { } }
        public event EventHandler? FavoritesChanged { add { } remove { } }

        public Task ScanAsync(IEnumerable<string> folders, CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseActiveScanForShutdownAsync(TimeSpan timeout) => Task.CompletedTask;
        public Task ImportFilesAsync(IEnumerable<string> filePaths, CancellationToken ct = default, IProgress<int>? progress = null) => Task.CompletedTask;
        public Track? GetTrackById(Guid id) => null;
        public Album? GetAlbumById(Guid id) => null;
        public IReadOnlyList<Album> GetAlbumsByArtist(string artistName) => Array.Empty<Album>();
        public Task RemoveTrackAsync(Guid id) => Task.CompletedTask;
        public Task RemoveTracksAsync(IEnumerable<Guid> ids) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<Guid, Guid>> RelocateTracksAsync(
            IReadOnlyList<(string oldPath, string newPath)> moves, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(new Dictionary<Guid, Guid>());
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
        public Task RebuildIndexAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void NotifyFavoritesChanged() { }
        public Task SetTracksRatingAsync(IReadOnlyList<Track> tracks, int rating) => Task.CompletedTask;
        public Task SetTracksDislikedAsync(IReadOnlyList<Track> tracks, bool isDisliked) => Task.CompletedTask;
        public Task SetTracksSnoozedAsync(IReadOnlyList<Track> tracks, DateTime? until) => Task.CompletedTask;
        public void NotifyMetadataChanged() { }
    }
}
