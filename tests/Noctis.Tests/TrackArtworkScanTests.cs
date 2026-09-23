using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Per-track covers end to end (Discord, veil 2026-09-22): "i embedded some specific songs
/// within albums with a different cover art than the rest but noctis just displays one
/// artwork for all of them". A scan gives the odd track its own cover; re-tagging it to
/// match the album takes it away again.
/// </summary>
[Collection("MetadataServiceStatics")]
public class TrackArtworkScanTests : IDisposable
{
    private static readonly byte[] AlbumArt = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 1, 1, 1 };
    private static readonly byte[] OddArt = { 0xFF, 0xD8, 0xFF, 0xE0, 7, 7, 7, 7, 7 };

    private readonly string _musicDir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public TrackArtworkScanTests() => Directory.CreateDirectory(_musicDir);

    public void Dispose()
    {
        try { Directory.Delete(_musicDir, true); } catch { }
    }

    private string CreateWav(string name, int trackNo, byte[]? art)
    {
        var path = Path.Combine(_musicDir, name);
        using (var fs = File.Create(path))
            SilentWavFile.Write(fs, seconds: 1, sampleRate: 8000, channels: 1);
        WriteTags(path, trackNo, art);
        return path;
    }

    private static void WriteTags(string path, int trackNo, byte[]? art)
    {
        using var f = TagLib.File.Create(path);
        f.Tag.Album = "Mixed Covers";
        f.Tag.Performers = new[] { "Cover Artist" };
        f.Tag.AlbumArtists = new[] { "Cover Artist" };
        f.Tag.Title = Path.GetFileNameWithoutExtension(path);
        f.Tag.Track = (uint)trackNo;
        f.Tag.Pictures = art == null
            ? Array.Empty<TagLib.IPicture>()
            : new TagLib.IPicture[]
            {
                new TagLib.Picture(new TagLib.ByteVector(art))
                    { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" }
            };
        f.Save();
    }

    private static string Own(IPersistenceService p, Guid trackId) => p.GetTrackArtworkPath(trackId);

    private static (LibraryService Library, TestPersistence Persistence) MakeLibrary()
    {
        var persistence = new TestPersistence();
        var index = new SqliteLibraryIndexService(persistence);
        var library = new LibraryService(new MetadataService(), persistence, index, new FakeAuditTrail());
        return (library, persistence);
    }

    [Fact]
    public async Task OddTrack_GetsItsOwnCover_TheRestShowTheAlbums()
    {
        CreateWav("1.wav", 1, AlbumArt);
        CreateWav("2.wav", 2, OddArt);
        CreateWav("3.wav", 3, AlbumArt);
        var (library, persistence) = MakeLibrary();
        using (persistence)
        {
            await library.ScanAsync(new[] { _musicDir });
            Assert.Equal(3, library.Tracks.Count);
            var odd = library.Tracks.Single(t => t.Title == "2");
            var album = persistence.GetArtworkPath(odd.AlbumId);

            Assert.Equal(AlbumArt, File.ReadAllBytes(album));
            var own = Own(persistence, odd.Id);
            Assert.Equal(own, odd.AlbumArtworkPath);
            Assert.Equal(OddArt, File.ReadAllBytes(own));
            Assert.All(library.Tracks.Where(t => t.Title != "2"), t => Assert.Equal(album, t.AlbumArtworkPath));
        }
    }

    [Fact]
    public async Task RetaggingTheOddTrackToMatch_DropsItsOwnCover()
    {
        CreateWav("1.wav", 1, AlbumArt);
        var oddPath = CreateWav("2.wav", 2, OddArt);
        CreateWav("3.wav", 3, AlbumArt);
        var (library, persistence) = MakeLibrary();
        using (persistence)
        {
            await library.ScanAsync(new[] { _musicDir });
            var oddId = library.Tracks.Single(t => t.Title == "2").Id;
            Assert.True(File.Exists(Own(persistence, oddId)));

            WriteTags(oddPath, 2, AlbumArt);
            File.SetLastWriteTimeUtc(oddPath, DateTime.UtcNow.AddMinutes(1));
            await library.ScanAsync(new[] { _musicDir });

            var odd = library.Tracks.Single(t => t.Title == "2");
            Assert.False(File.Exists(Own(persistence, oddId)));
            Assert.Equal(persistence.GetArtworkPath(odd.AlbumId), odd.AlbumArtworkPath);
            Assert.Equal(AlbumArt, File.ReadAllBytes(persistence.GetArtworkPath(odd.AlbumId)));
        }
    }

    [Fact]
    public async Task EmbeddedArtworkOff_NoTrackGetsItsOwn()
    {
        CreateWav("1.wav", 1, AlbumArt);
        CreateWav("2.wav", 2, OddArt);
        var (library, persistence) = MakeLibrary();
        using (persistence)
        {
            var was = MetadataService.UseEmbeddedArtwork;
            // The scan re-applies the persisted setting, so switch it off there.
            persistence.Settings.UseEmbeddedArtwork = false;
            try
            {
                await library.ScanAsync(new[] { _musicDir });
                Assert.All(library.Tracks, t => Assert.False(File.Exists(Own(persistence, t.Id))));
            }
            finally
            {
                MetadataService.UseEmbeddedArtwork = was;
            }
        }
    }

    private sealed class TestPersistence : IPersistenceService, IDisposable
    {
        public string DataDirectory { get; }
        public AppSettings Settings { get; set; } = new() { MetadataSchemaVersion = int.MaxValue };

        public TestPersistence()
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
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
        public Task SaveQueuePositionAsync(Guid? currentTrackId, double positionSeconds) => Task.CompletedTask;
        public Task<LibraryIndexCache?> LoadIndexCacheAsync() => Task.FromResult<LibraryIndexCache?>(null);
        public Task SaveIndexCacheAsync(LibraryIndexCache cache) => Task.CompletedTask;

        public string GetArtworkPath(Guid albumId) => Path.Combine(DataDirectory, "artwork", $"{albumId}.jpg");
        public void SaveArtwork(Guid albumId, byte[] imageData) => File.WriteAllBytes(GetArtworkPath(albumId), imageData);

        public string GetAnimatedCoverPath(Guid albumId, Guid? trackId, string extension)
            => Path.Combine(DataDirectory, "animated_covers", $"{albumId}.mp4");
        public void EnsureAnimatedCoverDir() { }

        public void Dispose()
        {
            try { if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, true); } catch { }
        }
    }

    private sealed class FakeAuditTrail : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
