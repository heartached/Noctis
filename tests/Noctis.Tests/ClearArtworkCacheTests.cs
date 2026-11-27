using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.Data.Sqlite;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Advanced › Clear Artwork Cache (owner 11-27: "make sure these 2 options are up to
/// date and in sync with everything in the app"). It deleted artwork/ and the thumbnails, but
/// every decoded cover and its colours stayed in memory (a cover saved again at the same path
/// came back from before), the albums kept pointing at the deleted files, and nothing took the
/// covers in the songs back until the next scan.
/// </summary>
[Collection("ArtworkCache")]
public class ClearArtworkCacheTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", "clear-art-" + Guid.NewGuid().ToString("N"));

    public ClearArtworkCacheTests()
    {
        Directory.CreateDirectory(_root);
        ArtworkCache.DisposeGrace = TimeSpan.Zero;
        ArtworkCache.ClearForTests();
    }

    public void Dispose()
    {
        ArtworkCache.ClearForTests();
        ArtworkCache.DecoderOverride = null;
        ArtworkCache.DisposeGrace = TimeSpan.FromSeconds(2);
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private string Touch(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private string WritePng(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(new SKColor(200, 30, 40));
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [AvaloniaFact]
    public async Task Clear_EmptiesTheArtworkFolders_AndKeepsTheOtherPictures()
    {
        var cleared = new[]
        {
            "artwork/album.jpg", "artwork/tracks/song.jpg", "artwork/artists/a.jpg", "artwork/artists/a.custom",
            "artwork/artists/b.removed", "artwork/similar/s.jpg",
        };
        // Not artwork caches (SettingsViewModel.ArtworkCacheFolders): kept.
        var kept = new[]
        {
            "animated_covers/album.mp4", "animated_covers/cache/auto.mp4", "playlist_covers/p.jpg",
            "profile/avatar.jpg", "server/covers/c.jpg", "cache/waveforms/w.bin", "artist_info/a.json",
        };
        foreach (var f in cleared.Concat(kept)) Touch(f);
        var vm = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();

        await vm.ClearArtworkCacheCommand.ExecuteAsync(null);

        foreach (var f in cleared) Assert.False(File.Exists(Path.Combine(_root, f)), f);
        foreach (var f in kept) Assert.True(File.Exists(Path.Combine(_root, f)), f);
        // The services that write there made these once, at startup.
        foreach (var dir in SettingsViewModel.ArtworkCacheFolders)
            Assert.True(Directory.Exists(Path.Combine(_root, dir)), dir);
        Assert.Equal(Loc.T("Settings.ArtworkCacheCleared"), vm.StorageStatusText);
    }

    [AvaloniaFact]
    public void ForgetFolder_DropsTheCoversUnderIt_FromMemory_AndTellsTheControls()
    {
        ArtworkCache.DecoderOverride = (_, _) =>
            new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var artwork = Path.Combine(_root, "artwork");
        var album = Path.Combine(artwork, "album.jpg");
        var song = Path.Combine(artwork, "tracks", "song.jpg");
        var animated = Path.Combine(_root, "animated_covers", "a.jpg");
        var sibling = Path.Combine(_root, "artwork2", "b.jpg");
        foreach (var p in new[] { album, song, animated, sibling }) Assert.NotNull(ArtworkCache.LoadAndCache(p, 256));
        string? raised = null;
        void OnFolder(string f) => raised = f;
        ArtworkCache.FolderInvalidated += OnFolder;
        try
        {
            Assert.Equal(2, ArtworkCache.ForgetFolder(artwork));
        }
        finally
        {
            ArtworkCache.FolderInvalidated -= OnFolder;
        }

        Assert.Null(ArtworkCache.TryGet(album, 256));
        Assert.Null(ArtworkCache.TryGet(song, 256));
        Assert.NotNull(ArtworkCache.TryGet(animated, 256));
        Assert.NotNull(ArtworkCache.TryGet(sibling, 256)); // "artwork2" is not under "artwork"
        Assert.Equal(artwork, raised);
    }

    [AvaloniaFact]
    public async Task Clear_ForgetsTheColoursWorkedOutFromTheCovers()
    {
        var cover = WritePng("artwork/album.png");
        var kept = WritePng("animated_covers/a.png");
        ArtworkVibrantColor.GetHex(cover);
        ArtworkVibrantColor.GetHex(kept);
        Assert.True(ArtworkVibrantColor.IsCached(cover));
        Assert.True(DominantColorExtractor.ExtractEdgeBackgroundColorFromFile(cover).HasValue);
        Assert.True(DominantColorExtractor.ExtractEdgeBackgroundColorFromFile(kept).HasValue);
        var vm = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();

        await vm.ClearArtworkCacheAsync();

        Assert.False(ArtworkVibrantColor.IsCached(cover));
        Assert.True(ArtworkVibrantColor.IsCached(kept));
        // The cover is gone: a colour still answered for it came from memory.
        Assert.False(DominantColorExtractor.ExtractEdgeBackgroundColorFromFile(cover).HasValue);
        Assert.True(DominantColorExtractor.ExtractEdgeBackgroundColorFromFile(kept).HasValue);
    }

    /// <summary>Reset Everything deletes cache/ with the thumbnail folder in it, which only a
    /// launch created: every later thumbnail failed to save.</summary>
    [Fact]
    public void Thumbnails_SaveAgain_AfterTheirFolderWasDeleted()
    {
        var thumbs = Path.Combine(_root, "cache", "artwork_thumbs");
        ArtworkThumbnailCache.Enable(thumbs);
        try
        {
            var cover = WritePng("artwork/big.png");
            Directory.Delete(Path.Combine(_root, "cache"), true);

            using (ArtworkThumbnailCache.DecodeToWidth(cover, 4)) { }

            Assert.True(Directory.Exists(thumbs));
            Assert.NotEmpty(Directory.GetFiles(thumbs));
        }
        finally
        {
            ArtworkThumbnailCache.Disable();
        }
    }
}

/// <summary>
/// Clear Artwork Cache with a real library: the albums lose the deleted covers at once, and the
/// covers in the songs' tags come back without a scan.
/// </summary>
[Collection("MetadataServiceStatics")]
public class ClearArtworkCacheLibraryTests : IDisposable
{
    private static readonly byte[] TaggedArt = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
    private static readonly byte[] DownloadedArt = { 0xFF, 0xD8, 0xFF, 0xE0, 9, 9, 9, 9 };

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", "clear-art-lib-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private string Wav(string dir, string name, string album, byte[]? art)
    {
        var path = Path.Combine(dir, name);
        using (var fs = File.Create(path))
            SilentWavFile.Write(fs, seconds: 1, sampleRate: 8000, channels: 1);
        using var f = TagLib.File.Create(path);
        f.Tag.Album = album;
        f.Tag.Performers = new[] { "Clear Artist" };
        f.Tag.Pictures = art == null
            ? Array.Empty<TagLib.IPicture>()
            : new TagLib.IPicture[]
            {
                new TagLib.Picture(new TagLib.ByteVector(art)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" },
            };
        f.Save();
        return path;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), what);
    }

    [AvaloniaFact]
    public async Task Clear_DropsTheGoneCoversFromTheAlbums_AndTakesTheTaggedOnesBackAtOnce()
    {
        var music = Path.Combine(_root, "Music");
        var data = Path.Combine(_root, "Data");
        Directory.CreateDirectory(music);
        Directory.CreateDirectory(data);
        Wav(music, "tagged.wav", "Tagged Album", TaggedArt);
        Wav(music, "plain.wav", "Plain Album", null);
        var persistence = new PersistenceService(data);
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new AuditTrailService(persistence));
        await library.ScanAsync(new[] { music });

        Album AlbumNamed(string name) => library.Albums.First(a => a.Name == name);
        var taggedArt = persistence.GetArtworkPath(AlbumNamed("Tagged Album").Id);
        var plainArt = persistence.GetArtworkPath(AlbumNamed("Plain Album").Id);
        await Until(() => File.Exists(taggedArt), "the scan cached the tagged cover");
        // A cover found online for the album without one: only in the data folder.
        persistence.SaveArtwork(AlbumNamed("Plain Album").Id, DownloadedArt);
        library.NotifyMetadataChanged();
        await Until(() => AlbumNamed("Plain Album").ArtworkPath == plainArt, "the downloaded cover is on its album");

        var vm = new SettingsViewModel(persistence, library, new NoOpPlayHistoryService());
        await vm.LoadAsync();
        await vm.ClearArtworkCacheAsync();

        await Until(() => AlbumNamed("Plain Album").ArtworkPath == null, "the gone cover left its album");
        await Until(() => File.Exists(taggedArt) && AlbumNamed("Tagged Album").ArtworkPath == taggedArt,
            "the tagged cover came back without a scan");
        Assert.Equal(TaggedArt, File.ReadAllBytes(taggedArt));
        Assert.False(File.Exists(plainArt));
        Assert.All(library.Tracks.Where(t => t.Album == "Plain Album"), t => Assert.Null(t.AlbumArtworkPath));
    }
}
