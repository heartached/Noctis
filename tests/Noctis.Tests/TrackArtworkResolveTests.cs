using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Per-track covers (Discord, veil 2026-09-22): a track whose embedded cover differs from
/// the rest of its album shows its own cover. <see cref="TrackArtwork.Resolve"/> decides
/// which cover is the album's and which tracks are the odd ones out.
/// </summary>
public class TrackArtworkResolveTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();

    [Fact]
    public void Fingerprint_SameBytesMatch_DifferentBytesDont_NoneIsNull()
    {
        Assert.Equal(TrackArtwork.Fingerprint(new byte[] { 1, 2, 3 }), TrackArtwork.Fingerprint(new byte[] { 1, 2, 3 }));
        Assert.NotEqual(TrackArtwork.Fingerprint(new byte[] { 1, 2, 3 }), TrackArtwork.Fingerprint(new byte[] { 1, 2, 4 }));
        Assert.Null(TrackArtwork.Fingerprint(null));
        Assert.Null(TrackArtwork.Fingerprint(Array.Empty<byte>()));
    }

    [Fact]
    public void MajorityCoverIsTheAlbums_TheOddTrackGetsItsOwn()
    {
        var (album, odd) = TrackArtwork.Resolve(new[] { (A, "x"), (B, "x"), (C, "y"), (D, "x") }, currentAlbumHash: "x");
        Assert.Equal("x", album);
        Assert.Equal(new[] { C }, odd);
    }

    [Fact]
    public void TheOddTrackWinningTheFirstClaimRace_IsCorrected()
    {
        // The scan caches whichever track it read first; if that was the odd one, the
        // majority still takes the album cover back.
        var (album, odd) = TrackArtwork.Resolve(new[] { (A, "x"), (B, "x"), (C, "y") }, currentAlbumHash: "y");
        Assert.Equal("x", album);
        Assert.Equal(new[] { C }, odd);
    }

    [Fact]
    public void ATie_KeepsTheCurrentAlbumCover()
    {
        var (album, odd) = TrackArtwork.Resolve(new[] { (A, "x"), (B, "y") }, currentAlbumHash: "y");
        Assert.Equal("y", album);
        Assert.Equal(new[] { A }, odd);
    }

    [Fact]
    public void TracksWithoutEmbeddedArt_NeitherVoteNorGetTheirOwn()
    {
        var (album, odd) = TrackArtwork.Resolve(new[] { (A, "x"), (B, (string?)null), (C, null) }, currentAlbumHash: "x");
        Assert.Equal("x", album);
        Assert.Empty(odd);

        // No embedded art anywhere (folder cover.jpg album): nothing changes.
        var (none, noOdd) = TrackArtwork.Resolve(new[] { (A, (string?)null) }, currentAlbumHash: "folder");
        Assert.Equal("folder", none);
        Assert.Empty(noOdd);
    }

    [Fact]
    public void AllTheSame_NoOddTracks()
    {
        var (album, odd) = TrackArtwork.Resolve(new[] { (A, "x"), (B, "x") }, currentAlbumHash: null);
        Assert.Equal("x", album);
        Assert.Empty(odd);
    }
}
