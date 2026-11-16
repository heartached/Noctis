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
    [InlineData(1100, 5, 212)]   // 1100/5 - 8 (1.6.9 tile chrome, the default)
    [InlineData(1650, 8, 198.25)]
    public void ComputeTileSize_FillsRowMinusChrome(double width, int columns, double expected)
        => Assert.Equal(expected, AlbumGridMetrics.ComputeTileSize(width, columns), 3);

    [Theory]
    [InlineData(1100, 5, 202)]   // 1100/5 - 18 (GitHub #210 tile chrome, "Wider spacing between albums")
    [InlineData(1650, 8, 188.25)]
    public void ComputeTileSize_WideSpacing_FillsRowMinusWideChrome(double width, int columns, double expected)
        => Assert.Equal(expected, AlbumGridMetrics.ComputeTileSize(width, columns, wideSpacing: true), 3);

    [Fact]
    public void ComputeTileSize_NeverBelowLegibilityFloor()
        => Assert.Equal(80, AlbumGridMetrics.ComputeTileSize(300, 20));

    // A Home album tile as layout rounding sizes it: the cover rounded up to a device pixel,
    // each side's margin and padding snapped to whole device pixels (either midpoint rule).
    private static double RoundedTileWidth(double cover, bool wide, double scale, MidpointRounding mode)
    {
        double Snap(double v) => Math.Round(v * scale, mode) / scale;
        var (margin, padding) = wide ? (3.0, 6.0) : (0.0, 4.0);
        return Math.Ceiling(cover * scale) / scale + 2 * (Snap(margin) + Snap(padding));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public void ComputeWrappedTileSize_FiveRoundedTilesAlwaysFitTheRow(double scale)
    {
        foreach (var wide in new[] { false, true })
        foreach (var mode in new[] { MidpointRounding.ToEven, MidpointRounding.AwayFromZero })
        for (var row = 700.0; row <= 2600; row += 0.8)
        {
            var cover = AlbumGridMetrics.ComputeWrappedTileSize(row, 5, wide, scale);
            var five = 5 * RoundedTileWidth(cover, wide, scale, mode);
            Assert.True(five <= row + 1e-9, $"scale {scale} wide {wide} row {row}: {five}");
        }
    }

    [Fact]
    public void ComputeTileSize_At125Percent_WideSpacing_OverflowsAWrappedRow()
    {
        // GitHub #242's cause: the even-split maths (and Home's old 2px slack) ignore that
        // 125% snaps the 3px margin to 3.2 and the 6px padding to 6.4, so five wide tiles
        // need ~6px more than the row has and the fifth wraps.
        const double row = 1200;
        var cover = AlbumGridMetrics.ComputeTileSize(row - 2, 5, wideSpacing: true);
        Assert.True(5 * RoundedTileWidth(cover, true, 1.25, MidpointRounding.ToEven) > row);
    }
}
