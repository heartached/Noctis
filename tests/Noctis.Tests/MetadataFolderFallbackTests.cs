using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// ReadTrackMetadata must fill missing artist/album from the folder structure
/// and missing track numbers from the "NN " filename prefix — real tags always
/// win. Shares the statics collection because it flips MusicRootFolders.
/// </summary>
[Collection("MetadataServiceStatics")]
public class MetadataFolderFallbackTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly string[] _savedRoots;

    public MetadataFolderFallbackTests()
    {
        Directory.CreateDirectory(_dir);
        _savedRoots = MetadataService.MusicRootFolders;
    }

    public void Dispose()
    {
        MetadataService.MusicRootFolders = _savedRoots;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string CreateWav(params string[] relative)
    {
        var path = Path.Combine(_dir, Path.Combine(relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        SilentWavFile.Write(fs, seconds: 1, sampleRate: 8000, channels: 1);
        return path;
    }

    [Fact]
    public void UntaggedWav_TakesIdentityFromFolders()
    {
        var root = Path.Combine(_dir, "Music");
        MetadataService.MusicRootFolders = new[] { root };
        var path = CreateWav("Music", "Folder Artist", "Folder Album", "01 Folder Song.wav");

        var track = new MetadataService().ReadTrackMetadata(path);

        Assert.NotNull(track);
        Assert.Equal("Folder Artist", track!.Artist);
        Assert.Equal("Folder Artist", track.AlbumArtist);
        Assert.Equal("Folder Album", track.Album);
        Assert.Equal("Folder Song", track.Title);
        Assert.Equal(1, track.TrackNumber);
        Assert.Equal(Track.ComputeAlbumId("Folder Artist", "Folder Album"), track.AlbumId);
    }

    [Fact]
    public void TaggedWav_IgnoresFolderNames()
    {
        var root = Path.Combine(_dir, "Music");
        MetadataService.MusicRootFolders = new[] { root };
        var path = CreateWav("Music", "Folder Artist", "Folder Album", "05 Song.wav");
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Performers = new[] { "Real Artist" };
            f.Tag.Album = "Real Album";
            f.Tag.Title = "Real Title";
            f.Tag.Track = 9;
            f.Save();
        }

        var track = new MetadataService().ReadTrackMetadata(path);

        Assert.NotNull(track);
        Assert.Equal("Real Artist", track!.Artist);
        Assert.Equal("Real Album", track.Album);
        Assert.Equal("Real Title", track.Title);
        Assert.Equal(9, track.TrackNumber);
    }
}
