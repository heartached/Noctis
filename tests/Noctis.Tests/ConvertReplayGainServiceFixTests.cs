using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08 "find and fix bugs" on Convert Album / Scan ReplayGain, the two found in the
/// shared services rather than the dialogs.
/// </summary>
public class ConvertReplayGainServiceFixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-convfix-" + Guid.NewGuid().ToString("N")[..8]);

    public ConvertReplayGainServiceFixTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static Track T(string album, string albumArtist = "Drake") => new()
    {
        Id = Guid.NewGuid(), Title = "x", Album = album, AlbumArtist = albumArtist,
        AlbumId = Track.ComputeAlbumId(string.IsNullOrWhiteSpace(albumArtist) ? "Unknown Artist" : albumArtist,
                                       string.IsNullOrWhiteSpace(album) ? "Unknown Album" : album),
    };

    /// <summary>Untagged singles all share one AlbumId; album gain grouped them into one
    /// pretend album. Each is now its own album, while real albums still group.</summary>
    [Fact]
    public void AlbumGain_UntaggedTracksAreEachTheirOwnAlbum_RealAlbumsStillGroup()
    {
        var a1 = T("Views");
        var a2 = T("Views");
        Assert.Equal(ReplayGainScannerService.AlbumGroupKey(a1), ReplayGainScannerService.AlbumGroupKey(a2));

        var loose1 = T("", "");
        var loose2 = T("", "");
        var literal = T("Unknown Album", "");
        Assert.Equal(loose1.AlbumId, loose2.AlbumId);           // the shared bucket
        Assert.Equal(Track.UnknownAlbumBucketId, loose1.AlbumId);
        Assert.NotEqual(ReplayGainScannerService.AlbumGroupKey(loose1), ReplayGainScannerService.AlbumGroupKey(loose2));
        Assert.Equal(loose1.Id, ReplayGainScannerService.AlbumGroupKey(loose1));
        Assert.Equal(literal.Id, ReplayGainScannerService.AlbumGroupKey(literal));
    }

    /// <summary>A cancelled/failed conversion left ffmpeg's half-written file behind.</summary>
    [Fact]
    public async Task PartialOutput_IsRemoved_AndAMissingFileIsFine()
    {
        var partial = Path.Combine(_dir, "half.mp3");
        await File.WriteAllBytesAsync(partial, new byte[1234]);
        await AudioConverterService.DeletePartialOutputAsync(partial);
        Assert.False(File.Exists(partial));

        await AudioConverterService.DeletePartialOutputAsync(Path.Combine(_dir, "never-written.mp3")); // no throw
    }

    /// <summary>The handle of a just-killed ffmpeg can linger: the delete retries instead of
    /// giving up on the first sharing violation.</summary>
    [Fact]
    public async Task PartialOutput_StillLocked_IsRemovedOnceReleased()
    {
        var partial = Path.Combine(_dir, "locked.mp3");
        var handle = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var delete = AudioConverterService.DeletePartialOutputAsync(partial);
        await Task.Delay(150);
        handle.Dispose();
        await delete;
        Assert.False(File.Exists(partial));
    }
}
