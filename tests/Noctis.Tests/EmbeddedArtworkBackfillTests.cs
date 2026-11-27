using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Covers the missing-artwork backfill that heals albums indexed without a cached
/// cover. Scans only extract art for new/changed files plus one post-scan pass, so
/// an interruption or a file that was unreadable at that moment left an album
/// artless forever: rescans skip unchanged files by mtime, and a no-change scan
/// returned before the artwork pass entirely. The backfill runs after library load
/// (and from no-change scans) and reads files only for albums that still have no
/// cached cover — which is how a library whose files carry only embedded tag art
/// (ID3 APIC etc.) finally gets its covers without a forced full rescan.
/// </summary>
[Collection("MetadataServiceStatics")]
public class EmbeddedArtworkBackfillTests : IDisposable
{
    private static readonly byte[] EmbeddedBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
    private static readonly byte[] CoverBytes = { 0x89, 0x50, 0x4E, 0x47, 5, 6, 7, 8 };

    private readonly string _musicDir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public EmbeddedArtworkBackfillTests() => Directory.CreateDirectory(_musicDir);

    public void Dispose()
    {
        try { Directory.Delete(_musicDir, true); } catch { }
    }

    /// <summary>Writes a real WAV whose ID3v2 tag carries a front-cover picture.</summary>
    private string CreateWavWithEmbeddedArt(string name, string album)
    {
        var path = Path.Combine(_musicDir, name);
        using (var fs = File.Create(path))
            SilentWavFile.Write(fs, seconds: 1, sampleRate: 8000, channels: 1);

        using var f = TagLib.File.Create(path);
        f.Tag.Album = album;
        f.Tag.Performers = new[] { "Backfill Artist" };
        f.Tag.Pictures = new TagLib.IPicture[]
        {
            new TagLib.Picture(new TagLib.ByteVector(EmbeddedBytes))
                { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" }
        };
        f.Save();
        return path;
    }

    private static Track MakeTrack(string filePath, string album) => new()
    {
        Id = Guid.NewGuid(),
        FilePath = filePath,
        Title = Path.GetFileNameWithoutExtension(filePath),
        Artist = "Backfill Artist",
        AlbumArtist = "Backfill Artist",
        Album = album,
        AlbumId = Track.ComputeAlbumId("Backfill Artist", album),
        Duration = TimeSpan.FromSeconds(1),
        FileSize = 100,
        LastModified = DateTime.UtcNow,
        DateAdded = DateTime.UtcNow,
        SourceType = SourceType.Local
    };

    private static (LibraryService Library, ArtworkTestPersistence Persistence) MakeLibrary(params Track[] tracks)
    {
        var persistence = new ArtworkTestPersistence { LibraryTracks = tracks.ToList() };
        var index = new SqliteLibraryIndexService(persistence);
        var library = new LibraryService(new MetadataService(), persistence, index, new FakeAuditTrail());
        return (library, persistence);
    }

    /// <summary>
    /// LoadAsync kicks the heal in the background; retry the explicit pass until one
    /// of them has cached the cover — both funnel through the same single-flight
    /// guard, so an explicit call during the background pass just returns 0.
    /// </summary>
    private static async Task HealUntilCachedAsync(LibraryService library, string artPath)
    {
        for (var i = 0; i < 100 && !File.Exists(artPath); i++)
        {
            await library.BackfillMissingArtworkAsync();
            if (!File.Exists(artPath))
                await Task.Delay(100);
        }
    }

    [Fact]
    public async Task BackfillMissingArtwork_CachesEmbeddedCoverForIndexedAlbum()
    {
        // The reported bug: an already-indexed library (unchanged files, so a scan
        // reuses every entry) whose albums have tag art only — covers must appear
        // without any file being touched or re-imported.
        const string album = "Tag Art Only";
        var track = MakeTrack(CreateWavWithEmbeddedArt("track1.wav", album), album);
        var (library, persistence) = MakeLibrary(track);
        using (persistence)
        {
            await library.LoadAsync();

            var artPath = persistence.GetArtworkPath(track.AlbumId);
            await HealUntilCachedAsync(library, artPath);

            Assert.True(File.Exists(artPath), "backfill never cached the embedded cover");
            Assert.Equal(EmbeddedBytes, File.ReadAllBytes(artPath));
        }
    }

    [Fact]
    public async Task BackfillMissingArtwork_LeavesExistingCachedCoverUntouched()
    {
        // Fill-once cache: an album that already has cached art must cost no file
        // reads and must never be overwritten by a later pass.
        const string album = "Already Cached";
        var track = MakeTrack(CreateWavWithEmbeddedArt("track1.wav", album), album);
        var (library, persistence) = MakeLibrary(track);
        using (persistence)
        {
            persistence.SaveArtwork(track.AlbumId, CoverBytes);

            await library.LoadAsync();
            var healed = await library.BackfillMissingArtworkAsync();

            Assert.Equal(0, healed);
            Assert.Equal(CoverBytes, File.ReadAllBytes(persistence.GetArtworkPath(track.AlbumId)));
        }
    }

    [Fact]
    public async Task BackfillMissingArtwork_EmbeddedDisabled_FallsBackToFolderCover()
    {
        // With "Use Embedded Artwork" off the backfill must ignore the tag picture
        // but still adopt a cover image sitting beside the album's tracks.
        const string album = "Folder Art Fallback";
        var track = MakeTrack(CreateWavWithEmbeddedArt("track1.wav", album), album);
        File.WriteAllBytes(Path.Combine(_musicDir, "cover.png"), CoverBytes);

        var (library, persistence) = MakeLibrary(track);
        // Both the static mirror and the persisted setting must say "off" — the
        // load path re-applies the persisted value to the mirror in the background.
        persistence.Settings.UseEmbeddedArtwork = false;
        MetadataService.UseEmbeddedArtwork = false;
        try
        {
            using (persistence)
            {
                await library.LoadAsync();

                var artPath = persistence.GetArtworkPath(track.AlbumId);
                await HealUntilCachedAsync(library, artPath);

                Assert.True(File.Exists(artPath), "backfill never cached the folder cover");
                Assert.Equal(CoverBytes, File.ReadAllBytes(artPath));
            }
        }
        finally
        {
            // Its background init copies this test's "off" into the static mirror; let it
            // finish first, or it lands in a later test's scan.
            await library.BackgroundInit;
            MetadataService.UseEmbeddedArtwork = true;
        }
    }

    [Fact]
    public async Task BackfillMissingArtwork_AlbumWithNoPictureAnywhere_IsNamedInTheSessionLog()
    {
        // 09-24 "some covers don't load": REVIVE+ / JACKBOYS / I'VE MINE - EP were FLACs
        // re-encoded without their picture block, in a folder with no cover image. The
        // note placeholder was right; the session log now says so, so the next report
        // can be answered from "Copy Logs" instead of the reporter's files.
        const string album = "No Picture Anywhere";
        var path = Path.Combine(_musicDir, "bare.wav");
        using (var fs = File.Create(path))
            SilentWavFile.Write(fs, seconds: 1, sampleRate: 8000, channels: 1);
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Album = album;
            f.Tag.Performers = new[] { "Backfill Artist" };
            f.Save();
        }
        var track = MakeTrack(path, album);
        var (library, persistence) = MakeLibrary(track);
        using (persistence)
        {
            await library.LoadAsync();
            var expected = $"no embedded picture and no folder cover image: {album} — Backfill Artist";
            for (var i = 0; i < 100 && !DebugLog.Snapshot().Contains(expected); i++)
            {
                await library.BackfillMissingArtworkAsync();
                await Task.Delay(50);
            }

            Assert.Contains(expected, DebugLog.Snapshot());
            Assert.False(File.Exists(persistence.GetArtworkPath(track.AlbumId)));
        }
    }

    /// <summary>
    /// Holds the second cover read of the session until released, so a test can act while a
    /// pass is part-way through its list (one album written, the next one being read).
    /// </summary>
    private sealed class GatedArtMetadata : MetadataService, IMetadataService
    {
        public readonly ManualResetEventSlim SecondReadStarted = new();
        public readonly ManualResetEventSlim Release = new();
        private int _reads;

        byte[]? IMetadataService.ExtractAlbumArt(string filePath)
        {
            if (Interlocked.Increment(ref _reads) == 2)
            {
                SecondReadStarted.Set();
                Release.Wait(TimeSpan.FromSeconds(30));
            }
            return EmbeddedBytes;
        }
    }

    [Fact]
    public async Task ClearDuringTheStartupHeal_EveryAlbumGetsItsTagCoverBackWithoutARescan()
    {
        // 11-27: Clear Artwork Cache while the startup heal was running. The heal had already
        // written the first album's cover; the clear deleted it and asked for a heal of its
        // own, which returned at the single-flight guard. The running heal only walked the
        // list it took at its start, so that album stayed bare until the next scan.
        var a = MakeTrack(CreateWavWithEmbeddedArt("a.wav", "Album A"), "Album A");
        var b = MakeTrack(CreateWavWithEmbeddedArt("b.wav", "Album B"), "Album B");
        var persistence = new ArtworkTestPersistence { LibraryTracks = new List<Track> { a, b } };
        var metadata = new GatedArtMetadata();
        var library = new LibraryService(metadata, persistence,
            new SqliteLibraryIndexService(persistence), new FakeAuditTrail());
        using (persistence)
        {
            await library.LoadAsync();

            // The startup heal: one album's cover written, the other one being read.
            Assert.True(metadata.SecondReadStarted.Wait(TimeSpan.FromSeconds(30)), "the startup heal never read a second cover");
            var artDir = Path.Combine(persistence.DataDirectory, "artwork");
            for (var i = 0; i < 300 && Directory.GetFiles(artDir).Length == 0; i++)
                await Task.Delay(10);
            Assert.Single(Directory.GetFiles(artDir));

            // Clear Artwork Cache: the folder is emptied, then a heal is asked for.
            foreach (var f in Directory.GetFiles(artDir)) File.Delete(f);
            await library.BackfillMissingArtworkAsync();

            metadata.Release.Set();
            await library.BackgroundInit;

            Assert.True(File.Exists(persistence.GetArtworkPath(a.AlbumId)), "Album A's tag cover was not taken back");
            Assert.True(File.Exists(persistence.GetArtworkPath(b.AlbumId)), "Album B's tag cover was not taken back");
            Assert.All(library.Albums.Where(x => x.Id == a.AlbumId || x.Id == b.AlbumId),
                album => Assert.NotNull(album.ArtworkPath));
        }
    }

    private sealed class ArtworkTestPersistence : IPersistenceService, IDisposable
    {
        public string DataDirectory { get; }

        public List<Track> LibraryTracks { get; set; } = new();

        // Schema already current — keeps LoadAsync's metadata backfills inert so the
        // artwork heal is the only background pass these tests observe.
        public AppSettings Settings { get; set; } = new() { MetadataSchemaVersion = int.MaxValue };

        public ArtworkTestPersistence()
        {
            DataDirectory = Path.Combine(
                Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(DataDirectory, "artwork"));
        }

        public bool LibraryLoadFailed => false;
        public string? LastCorruptFilePath => null;
        public bool SettingsLoadFailed => false;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);
        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public Task<List<Track>?> LoadLibraryAsync() => Task.FromResult<List<Track>?>(LibraryTracks);
        public Task SaveLibraryAsync(List<Track> tracks) => Task.CompletedTask;
        public Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(new List<Playlist>());
        public Task SavePlaylistsAsync(List<Playlist> playlists) => Task.CompletedTask;
        public Task<QueueState?> LoadQueueStateAsync() => Task.FromResult<QueueState?>(null);
        public Task SaveQueueStateAsync(QueueState state) => Task.CompletedTask;
        public Task SaveQueuePositionAsync(Guid? currentTrackId, double positionSeconds) => Task.CompletedTask;
        public Task<LibraryIndexCache?> LoadIndexCacheAsync() => Task.FromResult<LibraryIndexCache?>(null);
        public Task SaveIndexCacheAsync(LibraryIndexCache cache) => Task.CompletedTask;

        public string GetArtworkPath(Guid albumId) => Path.Combine(DataDirectory, "artwork", $"{albumId}.jpg");

        // Real write, unlike the other test persistences: the artwork file IS the
        // observable outcome of the backfill under test.
        public void SaveArtwork(Guid albumId, byte[] imageData)
            => File.WriteAllBytes(GetArtworkPath(albumId), imageData);

        public string GetAnimatedCoverPath(Guid albumId, Guid? trackId, string extension)
            => Path.Combine(DataDirectory, "animated_covers", $"{albumId}.mp4");
        public void EnsureAnimatedCoverDir() { }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DataDirectory))
                    Directory.Delete(DataDirectory, true);
            }
            catch
            {
                // Ignore cleanup race locks in tests.
            }
        }
    }

    private sealed class FakeAuditTrail : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
