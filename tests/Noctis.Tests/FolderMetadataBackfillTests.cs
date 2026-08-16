using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The v8 metadata-schema migration: libraries scanned before folder-derived
/// metadata existed hold every untagged file in the shared Unknown-Album
/// bucket. On load, those tracks must take artist/album from their stored
/// paths (pure string work — no file reads) and re-key their AlbumId.
/// </summary>
public class FolderMetadataBackfillTests : IDisposable
{
    private readonly BackfillTestPersistence _persistence = new();

    public void Dispose() => _persistence.Dispose();

    private static Track UntaggedBucketTrack(string path) => new()
    {
        Id = Guid.NewGuid(),
        FilePath = path,
        Title = System.IO.Path.GetFileNameWithoutExtension(path),
        Artist = "Unknown Artist",
        AlbumArtist = "Unknown Artist",
        Album = "Unknown Album",
        AlbumId = Track.UnknownAlbumBucketId,
        TrackNumber = 0,
        Duration = TimeSpan.FromMinutes(3),
        Bitrate = 1411,
        FileSize = 1000,
        SourceType = SourceType.Local,
        LastModified = DateTime.UtcNow,
        DateAdded = DateTime.UtcNow,
    };

    private LibraryService MakeLibrary() =>
        new(new MetadataService(), _persistence, new SqliteLibraryIndexService(_persistence), new FakeAuditTrail());

    // Generous: the migration rides behind SQLite init on a background task, which
    // crawls when the full suite runs in parallel (620 ms solo, 10s+ under load).
    private static async Task WaitUntil(Func<bool> condition, int budgetMs = 30000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
            await Task.Delay(50);
    }

    [Fact]
    public async Task Load_MigratesBucketTracksFromTheirPaths()
    {
        var track = UntaggedBucketTrack(TestPaths.Primary("Music", "Folder Artist", "Folder Album", "01 Song.wav"));
        _persistence.LibraryTracks.Add(track);
        _persistence.Settings.MusicFolders.Add(TestPaths.Primary("Music"));
        _persistence.Settings.MetadataSchemaVersion = 7; // pre-folder-metadata library

        var library = MakeLibrary();
        await library.LoadAsync();
        await WaitUntil(() => track.Artist == "Folder Artist"); // migration runs in background

        Assert.Equal("Folder Artist", track.Artist);
        Assert.Equal("Folder Album", track.Album);
        Assert.Equal("Song", track.Title);
        Assert.Equal(1, track.TrackNumber);
        Assert.Equal(Track.ComputeAlbumId("Folder Artist", "Folder Album"), track.AlbumId);

        // The migration's index rebuild must surface the real album.
        await WaitUntil(() => library.Albums.Any(a => a.Name == "Folder Album"));
        var album = Assert.Single(library.Albums);
        Assert.Equal("Folder Album", album.Name);
        Assert.Equal("Folder Artist", album.Artist);
    }

    [Fact]
    public async Task Load_UpToDateSchema_LeavesTracksAlone()
    {
        var track = UntaggedBucketTrack(TestPaths.Primary("Music", "Folder Artist", "Folder Album", "01 Song.wav"));
        _persistence.LibraryTracks.Add(track);
        _persistence.Settings.MetadataSchemaVersion = int.MaxValue;

        var library = MakeLibrary();
        await library.LoadAsync();
        await Task.Delay(400); // give the background pass a chance to (not) run

        Assert.Equal("Unknown Artist", track.Artist);
        Assert.Equal(Track.UnknownAlbumBucketId, track.AlbumId);
    }

    private sealed class BackfillTestPersistence : IPersistenceService, IDisposable
    {
        public string DataDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

        public List<Track> LibraryTracks { get; } = new();
        public AppSettings Settings { get; set; } = new();

        public BackfillTestPersistence() => Directory.CreateDirectory(DataDirectory);

        public bool LibraryLoadFailed => false;
        public string? LastCorruptFilePath => null;
        public bool SettingsLoadFailed => false;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);
        public Task SaveSettingsAsync(AppSettings settings) { Settings = settings; return Task.CompletedTask; }
        public Task<List<Track>?> LoadLibraryAsync() => Task.FromResult<List<Track>?>(LibraryTracks);
        public Task SaveLibraryAsync(List<Track> tracks) => Task.CompletedTask;
        public Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(new List<Playlist>());
        public Task SavePlaylistsAsync(List<Playlist> playlists) => Task.CompletedTask;
        public Task<QueueState?> LoadQueueStateAsync() => Task.FromResult<QueueState?>(null);
        public Task SaveQueueStateAsync(QueueState state) => Task.CompletedTask;
        public Task<LibraryIndexCache?> LoadIndexCacheAsync() => Task.FromResult<LibraryIndexCache?>(null);
        public Task SaveIndexCacheAsync(LibraryIndexCache cache) => Task.CompletedTask;
        public string GetArtworkPath(Guid albumId) => Path.Combine(DataDirectory, "artwork", $"{albumId}.jpg");
        public void SaveArtwork(Guid albumId, byte[] imageData) { }
        public string GetAnimatedCoverPath(Guid albumId, Guid? trackId, string extension) =>
            Path.Combine(DataDirectory, "animated_covers", $"{albumId}{extension}");
        public void EnsureAnimatedCoverDir() { }

        public void Dispose()
        {
            try { Directory.Delete(DataDirectory, true); } catch { }
        }
    }

    private sealed class FakeAuditTrail : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
