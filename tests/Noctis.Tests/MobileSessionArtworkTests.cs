using Noctis.Mobile.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The cover handed to the playback session (notification, lock screen) is shrunk when the
/// cached artwork is a full-size scan: Media3 copies the bytes on every player update, and a
/// 4000 px, 16.5 MB PNG ran a 192 MB heap out of memory when unreachable desktop songs were
/// passed over to a downloaded one (emulator logcat 10-20: OutOfMemoryError in
/// MediaMetadata.Builder.setArtworkData, a 16542913-byte allocation).
/// </summary>
public class MobileSessionArtworkTests
{
    [Fact]
    public void ASmallCover_IsHandedOverAsItIs()
    {
        Assert.False(SessionArtwork.NeedsShrinking(SessionArtwork.MaxBytes));
        Assert.False(SessionArtwork.NeedsShrinking(300_000));
    }

    [Fact]
    public void AFullSizeScan_IsShrunk()
    {
        Assert.True(SessionArtwork.NeedsShrinking(16_542_901));
        Assert.True(SessionArtwork.NeedsShrinking(SessionArtwork.MaxBytes + 1));
    }

    [Theory]
    [InlineData(4000, 4000, 4)]   // 1000 px
    [InlineData(3000, 2000, 4)]   // 750 px: 2 would leave 1500
    [InlineData(2048, 2048, 2)]   // exactly the limit
    [InlineData(1000, 1000, 1)]
    [InlineData(0, 0, 1)]         // bounds unreadable: decode as is
    [InlineData(-1, -1, 1)]
    public void SampleSize_BringsTheLongEdgeWithinTheLimit(int width, int height, int expected)
    {
        Assert.Equal(expected, SessionArtwork.SampleSize(width, height));
        if (width > 0) Assert.True(Math.Max(width, height) / expected <= SessionArtwork.MaxEdge);
    }
}
