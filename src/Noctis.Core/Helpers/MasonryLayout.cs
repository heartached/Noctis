namespace Noctis.Helpers;

/// <summary>
/// Placement for the Albums page's Masonry layout (GitHub #213): equal-width columns, each
/// card dropped into the currently shortest column (leftmost on a tie), so cards with a
/// one-line caption no longer leave a row-wide gap under them. Pure maths over the card
/// heights; the panel that shows it only realizes the cards near the viewport.
/// </summary>
public static class MasonryLayout
{
    /// <summary>Places <paramref name="heights"/> into <paramref name="columns"/> columns.
    /// Writes each card's column and top edge, and returns the tallest column's height.</summary>
    public static double Place(IReadOnlyList<double> heights, int columns, int[] column, double[] top)
    {
        if (columns < 1) columns = 1;
        Span<double> bottoms = columns <= 64 ? stackalloc double[columns] : new double[columns];
        bottoms.Clear();
        for (var i = 0; i < heights.Count; i++)
        {
            var c = 0;
            for (var k = 1; k < columns; k++)
                if (bottoms[k] < bottoms[c] - 0.01) c = k;
            column[i] = c;
            top[i] = bottoms[c];
            bottoms[c] += Math.Max(0, heights[i]);
        }
        var total = 0.0;
        foreach (var b in bottoms) total = Math.Max(total, b);
        return total;
    }

    /// <summary>
    /// Indexes of the cards that overlap the band [<paramref name="from"/>, <paramref name="to"/>).
    /// Every column's tops rise with the index, so a card far past the band ends the scan
    /// once all columns have passed it.
    /// </summary>
    public static void Visible(IReadOnlyList<double> heights, int[] column, double[] top, int columns,
        double from, double to, List<int> into)
    {
        into.Clear();
        if (columns < 1) columns = 1;
        var passed = 0;
        Span<bool> done = columns <= 64 ? stackalloc bool[columns] : new bool[columns];
        done.Clear();
        for (var i = 0; i < heights.Count && passed < columns; i++)
        {
            var y = top[i];
            if (y >= to)
            {
                if (!done[column[i]]) { done[column[i]] = true; passed++; }
                continue;
            }
            if (y + heights[i] > from) into.Add(i);
        }
    }
}
