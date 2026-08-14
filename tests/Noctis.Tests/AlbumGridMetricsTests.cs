using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

public class AlbumGridMetricsTests
{
    [Theory]
    [InlineData(800)]
    [InlineData(1650)]
    [InlineData(3400)]   // ultrawide: auto keeps the classic look
    public void ComputeColumns_AutoMode_AlwaysClassicFive(double width)
        => Assert.Equal(AlbumGridMetrics.ClassicColumns, AlbumGridMetrics.ComputeColumns(width, autoSize: true, targetSize: 220));

    [Theory]
    [InlineData(1100, 220, 5)]    // typical window at the default target = classic look
    [InlineData(1650, 220, 8)]    // maximized 1080p: more, same-sized covers
    [InlineData(3170, 220, 14)]   // ultrawide: covers stay near target instead of ballooning
    [InlineData(3170, 140, 20)]   // smallest covers on ultrawide hit the column ceiling (23 → 20)
    [InlineData(3170, 320, 10)]
    [InlineData(500, 320, 2)]     // narrow window with big covers: floor of 2 columns
    [InlineData(300, 320, 2)]     // rounding would give 1; floor keeps 2
    public void ComputeColumns_CustomSize_TracksTarget(double width, double target, int expected)
        => Assert.Equal(expected, AlbumGridMetrics.ComputeColumns(width, autoSize: false, targetSize: target));

    [Fact]
    public void ComputeColumns_TargetOutOfRange_IsClamped()
    {
        // 40px target would ask for 27 columns at 1100 wide; clamping to the 140
        // minimum gives round(1100/140) = 8.
        Assert.Equal(8, AlbumGridMetrics.ComputeColumns(1100, autoSize: false, targetSize: 40));
        // 900px target clamps to 320 → round(1100/320) = 3.
        Assert.Equal(3, AlbumGridMetrics.ComputeColumns(1100, autoSize: false, targetSize: 900));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-50)]
    public void ComputeColumns_InvalidWidth_FallsBackToClassic(double width)
        => Assert.Equal(AlbumGridMetrics.ClassicColumns, AlbumGridMetrics.ComputeColumns(width, autoSize: false, targetSize: 220));

    [Fact]
    public void ComputeColumns_NonFiniteTarget_DoesNotThrow()
        => Assert.InRange(AlbumGridMetrics.ComputeColumns(1100, autoSize: false, targetSize: double.NaN), 2, 20);

    [Theory]
    [InlineData(1100, 5, 212)]   // 1100/5 - 8
    [InlineData(1650, 8, 198.25)]
    public void ComputeTileSize_FillsRowMinusChrome(double width, int columns, double expected)
        => Assert.Equal(expected, AlbumGridMetrics.ComputeTileSize(width, columns), 3);

    [Fact]
    public void ComputeTileSize_NeverBelowLegibilityFloor()
        => Assert.Equal(80, AlbumGridMetrics.ComputeTileSize(300, 20));
}
