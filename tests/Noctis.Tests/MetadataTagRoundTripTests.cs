using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// On-disk tag-writing round-trips against real files (small generated WAVs —
/// TagLib gives them a full ID3v2 tag). Guards the two destructive-save fixes:
/// a plain save must not collapse multi-value genres, and saving artwork must
/// not wipe non-cover embedded pictures.
/// </summary>
public class MetadataTagRoundTripTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public MetadataTagRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string CreateWav(string name = "track.wav")
    {
        var path = Path.Combine(_dir, name);
        using var fs = File.Create(path);
        SilentWavFile.Write(fs, seconds: 1, sampleRate: 8000, channels: 1);
        return path;
    }

    private static readonly byte[] JpegBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
    private static readonly byte[] PngBytes = { 0x89, 0x50, 0x4E, 0x47, 5, 6, 7, 8 };

    [Fact]
    public void Save_WithUnchangedGenre_PreservesMultiValueGenres()
    {
        var path = CreateWav();
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Title = "Song";
            f.Tag.Performers = new[] { "Artist" };
            f.Tag.Genres = new[] { "Rock", "Jazz" };
            f.Save();
        }

        var svc = new MetadataService();
        var track = svc.ReadTrackMetadata(path);
        Assert.NotNull(track);
        Assert.Equal("Rock", track!.Genre); // model reads FirstGenre

        // Ordinary save with the genre untouched must keep both values.
        Assert.True(svc.WriteTrackMetadata(track));
        using (var f = TagLib.File.Create(path))
            Assert.Equal(new[] { "Rock", "Jazz" }, f.Tag.Genres);
    }

    [Fact]
    public void Save_WithChangedGenre_WritesTheNewSingleValue()
    {
        var path = CreateWav();
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Title = "Song";
            f.Tag.Genres = new[] { "Rock", "Jazz" };
            f.Save();
        }

        var svc = new MetadataService();
        var track = svc.ReadTrackMetadata(path)!;
        track.Genre = "Pop";

        Assert.True(svc.WriteTrackMetadata(track));
        using (var f = TagLib.File.Create(path))
            Assert.Equal(new[] { "Pop" }, f.Tag.Genres);
    }

    [Fact]
    public void WriteAlbumArt_ReplacesCover_KeepsBackCover()
    {
        var path = CreateWav();
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Pictures = new TagLib.IPicture[]
            {
                new TagLib.Picture(new TagLib.ByteVector(JpegBytes))
                    { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" },
                new TagLib.Picture(new TagLib.ByteVector(JpegBytes))
                    { Type = TagLib.PictureType.BackCover, MimeType = "image/jpeg" }
            };
            f.Save();
        }

        var svc = new MetadataService();
        Assert.True(svc.WriteAlbumArt(path, PngBytes));

        using (var f = TagLib.File.Create(path))
        {
            Assert.Equal(2, f.Tag.Pictures.Length);
            var front = f.Tag.Pictures[0];
            Assert.Equal(TagLib.PictureType.FrontCover, front.Type);
            Assert.Equal(PngBytes, front.Data.Data);
            Assert.Equal("image/png", front.MimeType);
            Assert.Contains(f.Tag.Pictures, p => p.Type == TagLib.PictureType.BackCover);
        }
    }

    [Fact]
    public void WriteAlbumArt_Remove_KeepsNonCoverPictures()
    {
        var path = CreateWav();
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Pictures = new TagLib.IPicture[]
            {
                new TagLib.Picture(new TagLib.ByteVector(JpegBytes))
                    { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" },
                new TagLib.Picture(new TagLib.ByteVector(JpegBytes))
                    { Type = TagLib.PictureType.BackCover, MimeType = "image/jpeg" }
            };
            f.Save();
        }

        var svc = new MetadataService();
        Assert.True(svc.WriteAlbumArt(path, null));

        using (var f = TagLib.File.Create(path))
        {
            var pic = Assert.Single(f.Tag.Pictures);
            Assert.Equal(TagLib.PictureType.BackCover, pic.Type);
        }
    }
}
