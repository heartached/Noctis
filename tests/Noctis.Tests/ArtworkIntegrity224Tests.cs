using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #224: seconds after Find Lyrics (saving into the audio files) an album tile showed
/// its cover's top third and flat grey below — a JPEG decoded from a file cut short. The real
/// cover ("In The Heat Of A Restless City", Kinnship) is cyan noise all over, so the top of the
/// tile was the right picture: the bytes simply ended. Three things let that happen, each
/// covered here: the cached cover was rewritten in place while the grid could be decoding it,
/// a lyrics-only rewrite of a song re-wrote the album's cached cover at all, and a truncated
/// JPEG decoded "successfully" (and could be kept as a thumbnail on disk).
/// </summary>
[Collection("MetadataServiceStatics")]
public class ArtworkIntegrity224Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "224-" + Guid.NewGuid().ToString("N"));

    public ArtworkIntegrity224Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        ArtworkThumbnailCache.Disable();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>A baseline JPEG of noise, like the reported cover: every byte of scan data matters.</summary>
    private static byte[] NoiseJpeg(int size = 600)
    {
        var rng = new Random(224);
        using var bitmap = new SKBitmap(size, size);
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            bitmap.SetPixel(x, y, new SKColor((byte)rng.Next(80, 160), (byte)rng.Next(200, 256), (byte)rng.Next(200, 256)));
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    [Fact]
    public void ATruncatedCoverIsNotDecoded()
    {
        var full = NoiseJpeg();
        var whole = Path.Combine(_dir, "whole.jpg");
        var cut = Path.Combine(_dir, "cut.jpg");
        File.WriteAllBytes(whole, full);
        File.WriteAllBytes(cut, full[..(full.Length / 3)]);

        using (var ok = Noctis.Helpers.SkiaArtworkDecoder.DecodeToWidth(whole, 256))
            Assert.NotNull(ok);
        Assert.Null(Noctis.Helpers.SkiaArtworkDecoder.DecodeToWidth(cut, 256));
        Assert.Null(Noctis.Helpers.SkiaArtworkDecoder.DecodeSubsampled(cut, 256));
    }

    [Fact]
    public void ATruncatedCoverLeavesNoThumbnailBehind()
    {
        var thumbs = Path.Combine(_dir, "thumbs");
        ArtworkThumbnailCache.Enable(thumbs);
        var cut = Path.Combine(_dir, "cut.jpg");
        var full = NoiseJpeg();
        File.WriteAllBytes(cut, full[..(full.Length / 3)]);

        Assert.Null(ArtworkThumbnailCache.DecodeToWidth(cut, 256));
        Assert.Empty(Directory.GetFiles(thumbs, "*.thumb"));
    }

    [Fact]
    public async Task ACoverBeingReadIsReplacedWholeNeverUnderTheReader()
    {
        // Skia opens a cover the way fopen does: sharing reads and writes, not delete.
        var root = Path.Combine(_dir, "data");
        var persistence = new PersistenceService(root);
        var album = Guid.NewGuid();
        var oldBytes = NoiseJpeg(300);
        var newBytes = NoiseJpeg(500);
        persistence.SaveArtwork(album, oldBytes);
        var path = persistence.GetArtworkPath(album);

        var seen = new byte[oldBytes.Length];
        Task save;
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var half = oldBytes.Length / 2;
            Assert.Equal(half, reader.Read(seen, 0, half));
            save = Task.Run(() => persistence.SaveArtwork(album, newBytes));
            await Task.Delay(150);
            reader.ReadExactly(seen, half, oldBytes.Length - half);
        }
        await save.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(oldBytes, seen);                    // the reader saw the old cover, whole
        Assert.Equal(newBytes, File.ReadAllBytes(path));  // and the new one still landed
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!)); // no temp left behind
    }

    [Fact]
    public void TheAppsOwnTagTempCopiesAreNotLibraryFiles()
    {
        Assert.True(MetadataService.IsOwnTempFile(@"C:\Music\A\.noctis-0123456789abcdef0123456789abcdef.mp3"));
        Assert.False(MetadataService.IsOwnTempFile(@"C:\Music\A\01 Song.mp3"));
        Assert.False(MetadataService.IsOwnTempFile(@"C:\Music\.noctis\01 Song.mp3"));
    }

    // ── Lyrics-only rewrites leave the album's cover alone ──

    private string CreateWav(string name, byte[] embeddedArt)
    {
        var path = Path.Combine(_dir, name);
        using (var fs = File.Create(path))
            SilentWavFile.Write(fs, seconds: 1, sampleRate: 8000, channels: 1);
        using var f = TagLib.File.Create(path);
        f.Tag.Album = "In The Heat Of A Restless City";
        f.Tag.Performers = new[] { "Kinnship" };
        f.Tag.Pictures = new TagLib.IPicture[]
        {
            new TagLib.Picture(new TagLib.ByteVector(embeddedArt)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" },
        };
        f.Save();
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavingLyricsIntoAFileDoesNotRewriteItsAlbumsCover(bool viaScan)
    {
        var embedded = NoiseJpeg(200);
        var music = Path.Combine(_dir, "music");
        Directory.CreateDirectory(music);
        var path = CreateWav(Path.Combine("music", "song.wav"), embedded);
        var root = Path.Combine(_dir, "data");
        var persistence = new PersistenceService(root);
        var metadata = new MetadataService();
        var library = new LibraryService(metadata, persistence, new SqliteLibraryIndexService(persistence), new NullAuditTrail());

        await library.ScanAsync(new[] { music });
        var track = Assert.Single(library.Tracks);
        var cover = persistence.GetArtworkPath(track.AlbumId);
        // The album's cover is not this file's picture: chosen in the app, or another song's.
        var chosen = NoiseJpeg(320);
        persistence.SaveArtwork(track.AlbumId, chosen);

        var replaced = new List<string>();
        void OnReplaced(string p) { lock (replaced) replaced.Add(p); }
        LibraryService.ArtworkFileReplaced += OnReplaced;
        try
        {
            Assert.True(metadata.WriteLyrics(path, "la la", "[00:00.50]la la"));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

            if (viaScan) await library.ScanAsync(new[] { music });
            else await library.ImportDiscoveredFilesAsync(new[] { path });
        }
        finally
        {
            LibraryService.ArtworkFileReplaced -= OnReplaced;
        }

        Assert.Equal(chosen, File.ReadAllBytes(cover));
        Assert.DoesNotContain(cover, replaced);
    }

    private sealed class NullAuditTrail : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
