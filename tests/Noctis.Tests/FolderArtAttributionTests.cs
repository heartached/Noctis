using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Guards which files may adopt a cover image sitting beside them on disk.
/// <para>
/// A file with no album tag is filed under the library-wide "Unknown Album" bucket
/// that every untagged file in the library shares, so a folder cover picked up for
/// it is written to that shared bucket and then shown for all of them — and it
/// sticks, because the cache is only filled once per album. Dropping a loose
/// untagged file that happened to sit inside an album's folder therefore stamped
/// that album's cover.png onto the Unknown bucket, which read as the app copying
/// the artwork of the track that was playing onto the newly dropped file.
/// </para>
/// </summary>
// Shares a collection with EmbeddedArtworkBackfillTests: both flip the
// MetadataService.UseEmbeddedArtwork static, which must not bleed into the
// other class mid-run (xUnit parallelizes across collections, not within).
[Collection("MetadataServiceStatics")]
public class FolderArtAttributionTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public FolderArtAttributionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static readonly byte[] CoverBytes = { 0x89, 0x50, 0x4E, 0x47, 5, 6, 7, 8 };
    private static readonly byte[] EmbeddedBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };

    /// <summary>Writes a real WAV; TagLib gives it a full ID3v2 tag.</summary>
    private string CreateWav(string name, string? album)
    {
        var path = Path.Combine(_dir, name);
        using (var fs = File.Create(path))
            SilentWavFile.Write(fs, seconds: 1, sampleRate: 8000, channels: 1);

        if (album != null)
        {
            using var f = TagLib.File.Create(path);
            f.Tag.Album = album;
            f.Save();
        }
        return path;
    }

    private string WriteFolderCover(string name = "cover.png")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, CoverBytes);
        return path;
    }

    [Fact]
    public void ExtractAlbumArt_UntaggedFileBesideAnAlbumCover_DoesNotAdoptIt()
    {
        // The reported bug: an untagged file dropped from an album's folder.
        WriteFolderCover();
        var stray = CreateWav("stray.wav", album: null);

        Assert.Null(new MetadataService().ExtractAlbumArt(stray));
    }

    [Theory]
    [InlineData("Unknown Album")]
    [InlineData("unknown album")]
    [InlineData("  Unknown Album  ")]
    public void ExtractAlbumArt_FileTaggedWithLiteralUnknownAlbum_DoesNotAdoptFolderCover(string album)
    {
        // Second field occurrence of the same bug: the file HAD an album tag — a
        // literal "Unknown Album" written by whatever exported it. That resolves to
        // the same shared bucket as no tag at all, so an emptiness check let the
        // folder cover through. The placeholder must be matched by value.
        WriteFolderCover();
        var file = CreateWav("placeholder.wav", album);

        Assert.Null(new MetadataService().ExtractAlbumArt(file));
    }

    [Fact]
    public void ExtractAlbumArt_TaggedFileBesideAnAlbumCover_StillAdoptsIt()
    {
        // Regression guard: folder art is the normal cover source for an album
        // folder whose tracks carry no embedded picture.
        WriteFolderCover();
        var track = CreateWav("track.wav", album: "No Me Conoce (Remix) - Single");

        Assert.Equal(CoverBytes, new MetadataService().ExtractAlbumArt(track));
    }

    private static void AttachEmbeddedArt(string path)
    {
        using var f = TagLib.File.Create(path);
        f.Tag.Pictures = new TagLib.IPicture[]
        {
            new TagLib.Picture(new TagLib.ByteVector(EmbeddedBytes))
                { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" }
        };
        f.Save();
    }

    [Fact]
    public void ExtractAlbumArt_UntaggedFileWithEmbeddedArt_StillReturnsTheEmbeddedArt()
    {
        // Embedded art belongs to the file itself, so the missing album tag is
        // irrelevant — only the folder-level guess is attribution-sensitive.
        WriteFolderCover();
        var path = CreateWav("embedded.wav", album: null);
        AttachEmbeddedArt(path);

        Assert.Equal(EmbeddedBytes, new MetadataService().ExtractAlbumArt(path));
    }

    [Fact]
    public void ExtractAlbumArt_EmbeddedArtworkDisabled_UsesFolderCoverInstead()
    {
        // "Use Embedded Artwork" off must not blind the folder-cover fallback —
        // it only removes the tag picture from consideration.
        WriteFolderCover();
        var path = CreateWav("toggled.wav", album: "Real Album");
        AttachEmbeddedArt(path);

        MetadataService.UseEmbeddedArtwork = false;
        try
        {
            Assert.Equal(CoverBytes, new MetadataService().ExtractAlbumArt(path));
        }
        finally
        {
            MetadataService.UseEmbeddedArtwork = true;
        }

        // Back on, the tag picture wins again (embedded has priority over folder).
        Assert.Equal(EmbeddedBytes, new MetadataService().ExtractAlbumArt(path));
    }

    [Fact]
    public void ReadTrackMetadata_EmbeddedArtworkDisabled_SuppressesInlineArt()
    {
        // The scan caches each album's first embedded cover straight from the parse;
        // the toggle must silence that inline channel too, not just ExtractAlbumArt.
        var path = CreateWav("inline.wav", album: "Real Album");
        AttachEmbeddedArt(path);

        MetadataService.UseEmbeddedArtwork = false;
        try
        {
            var track = new MetadataService().ReadTrackMetadata(path, out var art);
            Assert.NotNull(track);
            Assert.Null(art);
        }
        finally
        {
            MetadataService.UseEmbeddedArtwork = true;
        }

        Assert.NotNull(new MetadataService().ReadTrackMetadata(path, out var artOn));
        Assert.Equal(EmbeddedBytes, artOn);
    }
}
