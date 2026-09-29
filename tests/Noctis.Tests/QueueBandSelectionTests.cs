using Noctis.Helpers;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #88: rubber-band selection in the queue panel. The band is resolved to a row span
/// in MainWindow code-behind; <see cref="QueueRowSelection{T}.SelectBand"/> owns what that
/// span selects.
/// </summary>
public class QueueBandSelectionTests
{
    private static QueueRowSelection<Track> Rows(int count)
    {
        var rows = new List<Track>();
        for (var i = 0; i < count; i++) rows.Add(new Track { Title = $"t{i}", FilePath = $"C:/q/{i}.mp3" });
        return new QueueRowSelection<Track>(rows);
    }

    [Theory]
    [InlineData(1, 3, new[] { 1, 2, 3 })]
    [InlineData(3, 1, new[] { 1, 2, 3 })]   // dragged upward
    [InlineData(2, 2, new[] { 2 })]
    [InlineData(-1, 1, new[] { 0, 1 })]     // started in the list's top margin
    [InlineData(3, 9, new[] { 3, 4 })]      // ran past the last row
    public void Band_SelectsTheRowsItSpans_ClampedToTheQueue(int from, int to, int[] expected)
    {
        var sel = Rows(5);
        sel.SelectBand(from, to);
        Assert.Equal(expected, sel.Snapshot());
    }

    [Fact]
    public void Band_EntirelyBelowTheLastRow_SelectsNothing()
    {
        var sel = Rows(3);
        sel.SelectOnly(1);
        sel.SelectBand(5, 7);
        Assert.Empty(sel.Snapshot());
    }

    [Fact]
    public void Band_ReplacesTheSelection_UnlessRowsAreKept()
    {
        var sel = Rows(8);
        sel.SelectOnly(6);
        sel.SelectBand(1, 2);
        Assert.Equal(new[] { 1, 2 }, sel.Snapshot());

        // Ctrl/Shift band: the selection at band start survives.
        sel.SelectOnly(6);
        var keep = sel.Snapshot();
        sel.SelectBand(1, 2, keep);
        Assert.Equal(new[] { 1, 2, 6 }, sel.Snapshot());

        // Shrinking the band back gives rows up again (only the kept ones stay).
        sel.SelectBand(1, 1, keep);
        Assert.Equal(new[] { 1, 6 }, sel.Snapshot());
    }

    [Fact]
    public void Band_AnchorsAtItsStartRow_SoShiftClickExtendsFromThere()
    {
        var sel = Rows(8);
        sel.SelectBand(5, 3);          // dragged upward from row 5
        Assert.Equal(5, sel.Anchor);

        sel.SelectRangeTo(7);
        Assert.Equal(new[] { 5, 6, 7 }, sel.Snapshot());

        sel.SelectBand(-2, 1);         // start clamped into the queue
        Assert.Equal(0, sel.Anchor);
    }
}
