using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A cover changed AFTER the album was first scanned never reached the UI: the cached
/// <c>artwork/&lt;albumId&gt;.jpg</c> is written once and every writer short-circuited on
/// File.Exists, so a re-tagged embedded picture or a replaced cover.jpg was ignored
/// until the user wiped the cache. These cover the two refresh paths a rescan now takes:
/// a changed audio file with different embedded art, and a folder cover newer than the
/// cached cover on an otherwise unchanged album.
/// </summary>
[Collection("MetadataServiceStatics")]
public class ArtworkRefreshTests : IDisposable
{
    private static readonly byte[] ArtV1 = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 1, 1, 1 };
    private static readonly byte[] ArtV2 = { 0xFF, 0xD8, 0xFF, 0xE0, 2, 2, 2, 2, 2 };
    private static readonly byte[] FolderArtNew = { 0x89, 0x50, 0x4E, 0x47, 9, 9, 9, 9, 9, 9 };

    private readonly string _musicDir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public ArtworkRefreshTests() => Directory.CreateDirectory(_musicDir);

    public void Dispose()
    {
        try { Directory.Delete(_musicDir, true); } catch { }
    }

    private string CreateWav(string name, string album, byte[]? embeddedArt)
    {
        var path = Path.Combine(_musicDir, name);
        using (var fs = File.Create(path))
            SilentWavFile.Write(fs, seconds: 1, sampleRate: 8000, channels: 1);
        WriteTags(path, album, embeddedArt);
        return path;
    }

    private static void WriteTags(string path, string album, byte[]? embeddedArt)
    {
        using var f = TagLib.File.Create(path);
        f.Tag.Album = album;
        f.Tag.Performers = new[] { "Refresh Artist" };
        f.Tag.Pictures = embeddedArt == null
            ? Array.Empty<TagLib.IPicture>()
            : new TagLib.IPicture[]
            {
                new TagLib.Picture(new TagLib.ByteVector(embeddedArt))
                    { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" }
            };
        f.Save();
    }

    private static (LibraryService Library, ArtworkTestPersistence Persistence) MakeLibrary()
    {
        var persistence = new ArtworkTestPersistence();
        var index = new SqliteLibraryIndexService(persistence);
        var library = new LibraryService(new MetadataService(), persistence, index, new FakeAuditTrail());
        return (library, persistence);
    }

    [Fact]
    public async Task Rescan_after_embedded_art_changes_replaces_cached_cover()
    {
        var path = CreateWav("track.wav", "Refresh Album", ArtV1);
        var (library, persistence) = MakeLibrary();
        using (persistence)
        {
            await library.ScanAsync(new[] { _musicDir });
            var track = Assert.Single(library.Tracks);
            var artPath = persistence.GetArtworkPath(track.AlbumId);
            Assert.Equal(ArtV1, File.ReadAllBytes(artPath));

            // Re-tag with a different picture. Bump the mtime explicitly so the scan's
            // "unchanged by mtime + size" fast path cannot swallow the change on a
            // filesystem with coarse timestamps.
            WriteTags(path, "Refresh Album", ArtV2);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

            await library.ScanAsync(new[] { _musicDir });

            Assert.Equal(ArtV2, File.ReadAllBytes(artPath));
        }
    }

    [Fact]
    public async Task Rescan_with_no_audio_changes_picks_up_newer_folder_cover()
    {
        // No embedded art: the album's cover comes from the folder image.
        CreateWav("track.wav", "Folder Album", embeddedArt: null);
        var coverPath = Path.Combine(_musicDir, "cover.jpg");
        File.WriteAllBytes(coverPath, ArtV1);

        var (library, persistence) = MakeLibrary();
        using (persistence)
        {
            await library.ScanAsync(new[] { _musicDir });
            var track = Assert.Single(library.Tracks);
            var artPath = persistence.GetArtworkPath(track.AlbumId);
            Assert.Equal(ArtV1, File.ReadAllBytes(artPath));

            // Swap the folder cover; the audio is untouched, so the rescan is a "no
            // changes" scan — exactly the path that used to ignore the new image.
            File.WriteAllBytes(coverPath, FolderArtNew);
            File.SetLastWriteTimeUtc(coverPath, DateTime.UtcNow.AddMinutes(1));

            await library.ScanAsync(new[] { _musicDir });

            Assert.Equal(FolderArtNew, File.ReadAllBytes(artPath));
        }
    }

    [Fact]
    public async Task Unchanged_cover_is_left_alone()
    {
        CreateWav("track.wav", "Stable Album", ArtV1);
        var (library, persistence) = MakeLibrary();
        using (persistence)
        {
            await library.ScanAsync(new[] { _musicDir });
            var track = Assert.Single(library.Tracks);
            var artPath = persistence.GetArtworkPath(track.AlbumId);
            var stamp = File.GetLastWriteTimeUtc(artPath);

            await library.ScanAsync(new[] { _musicDir });

            Assert.Equal(ArtV1, File.ReadAllBytes(artPath));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(artPath));
        }
    }

    [Theory]
    [InlineData(@"C:\Music\Album\cover.jpg", true)]
    [InlineData(@"C:\Music\Album\Folder.PNG", true)]
    [InlineData(@"C:\Music\Album\front.webp", true)]
    [InlineData(@"C:\Music\Album\booklet.jpg", false)]
    [InlineData(@"C:\Music\Album\cover.txt", false)]
    [InlineData("", false)]
    public void Folder_art_candidate_names(string path, bool expected)
        => Assert.Equal(expected, MetadataService.IsFolderArtCandidate(path));

    private sealed class ArtworkTestPersistence : IPersistenceService, IDisposable
    {
        public string DataDirectory { get; }

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
        public Task<List<Track>?> LoadLibraryAsync() => Task.FromResult<List<Track>?>(new List<Track>());
        public Task SaveLibraryAsync(List<Track> tracks) => Task.CompletedTask;
        public Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(new List<Playlist>());
        public Task SavePlaylistsAsync(List<Playlist> playlists) => Task.CompletedTask;
        public Task<QueueState?> LoadQueueStateAsync() => Task.FromResult<QueueState?>(null);
        public Task SaveQueueStateAsync(QueueState state) => Task.CompletedTask;
        public Task<LibraryIndexCache?> LoadIndexCacheAsync() => Task.FromResult<LibraryIndexCache?>(null);
        public Task SaveIndexCacheAsync(LibraryIndexCache cache) => Task.CompletedTask;

        public string GetArtworkPath(Guid albumId) => Path.Combine(DataDirectory, "artwork", $"{albumId}.jpg");

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
