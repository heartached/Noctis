using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Noctis.Controls;

/// <summary>
/// The word row of a karaoke line: a <see cref="WrapPanel"/> that, for a line reading right to
/// left (<see cref="RightToLeft"/>, plugin API 1.5), lays its words out from the right: the first
/// word at the right edge, each row filled leftwards, wrapping as before. Only the slots are
/// mirrored, never the words themselves, so their text is drawn the right way round.
/// <see cref="KaraokeRestingWords"/> mirrors its cells the same way.
/// </summary>
public class LyricWrapPanel : WrapPanel
{
    public static readonly StyledProperty<bool> RightToLeftProperty =
        AvaloniaProperty.Register<LyricWrapPanel, bool>(nameof(RightToLeft));

    public bool RightToLeft { get => GetValue(RightToLeftProperty); set => SetValue(RightToLeftProperty, value); }

    // Avalonia's WrapPanel is horizontal-first; the type key keeps its styles.
    protected override Type StyleKeyOverride => typeof(WrapPanel);

    static LyricWrapPanel() => AffectsArrange<LyricWrapPanel>(RightToLeftProperty);

    protected override Size ArrangeOverride(Size finalSize)
    {
        // WrapPanel breaks the rows; each row's cells are then placed by LyricRowPlacement, the
        // rule the resting copy (KaraokeRestingWords) places them by too.
        var size = base.ArrangeOverride(finalSize);
        var children = Children;
        var dpi = UseLayoutRounding ? LayoutHelper.GetLayoutScale(this) : 0;
        var rowStart = 0;
        while (rowStart < children.Count)
        {
            var y = children[rowStart].Bounds.Y;
            var rowEnd = rowStart;
            var visible = 0;
            while (rowEnd < children.Count && children[rowEnd].Bounds.Y == y)
                if (children[rowEnd++].IsVisible) visible++;
            if (visible > 0) PlaceRow(rowStart, rowEnd, visible, finalSize.Width, dpi);
            rowStart = Math.Max(rowEnd, rowStart + 1);
        }
        return size;
    }

    private void PlaceRow(int start, int end, int visible, double available, double dpi)
    {
        var children = Children;
        Span<double> buffer = visible <= 64 ? stackalloc double[visible * 2] : new double[visible * 2];
        var widths = buffer[..visible];
        var xs = buffer[visible..];
        double firstLeft = 0, firstRight = 0, lastLeft = 0, lastRight = 0;
        var k = 0;
        for (var i = start; i < end; i++)
        {
            var cell = children[i];
            if (!cell.IsVisible) continue;
            var m = cell.Margin;
            widths[k] = cell.Bounds.Width + m.Left + m.Right;   // the slot WrapPanel gave it
            var (l, r) = GlyphsOf(cell);
            if (k == 0) (firstLeft, firstRight) = (l + m.Left, r + m.Left);
            (lastLeft, lastRight) = (l + m.Left, r + m.Left);
            k++;
        }
        LyricRowPlacement.Place(widths, firstLeft, firstRight, lastLeft, lastRight, available, ItemsAlignment, RightToLeft, dpi, xs);
        k = 0;
        for (var i = start; i < end; i++)
        {
            var cell = children[i];
            if (!cell.IsVisible) continue;
            var b = cell.Bounds;
            var m = cell.Margin;
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (xs[k] != b.X - m.Left)
                cell.Arrange(new Rect(xs[k], b.Y - m.Top, widths[k], b.Height + m.Top + m.Bottom));
            k++;
        }
    }

    /// <summary>Where a cell's glyphs start and end inside it: its word's (the "word-sweep" layer of
    /// the word cell template), scaled as the cell scales it; the whole cell for anything else.</summary>
    private static (double Left, double Right) GlyphsOf(Control cell)
    {
        var width = cell.Bounds.Width;
        var word = FindWord(cell);
        if (word is null || word.Bounds.Width <= 0) return (0, width);
        var scale = width / word.Bounds.Width;
        var (l, r) = word.GlyphSpan;
        return (l * scale, r * scale);
    }

    private static KaraokeWordText? FindWord(Visual visual)
    {
        if (visual is KaraokeWordText word && word.Classes.Contains("word-sweep")) return word;
        if (visual.GetVisualChildren() is not IReadOnlyList<Visual> children) return null;
        for (var i = 0; i < children.Count; i++)   // indexed: arranged often, allocates nothing
            if (FindWord(children[i]) is { } found) return found;
        return null;
    }
}
