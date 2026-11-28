using Avalonia;
using Avalonia.Controls;
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
        var size = base.ArrangeOverride(finalSize);
        if (RightToLeft) Mirror(finalSize.Width);
        AlignRowsByTheirGlyphs(finalSize.Width);
        return size;
    }

    private void Mirror(double width)
    {
        foreach (var child in Children)
        {
            // Bounds is the arranged box inside the margin: mirror the whole slot.
            var b = child.Bounds;
            var m = child.Margin;
            var x = b.X - m.Left;
            var w = b.Width + m.Left + m.Right;
            child.Arrange(new Rect(width - x - w, b.Y - m.Top, w, b.Height + m.Top + m.Bottom));
        }
    }

    /// <summary>
    /// WrapPanel aligns a row by its cells, and a row's last cell ends in blank: its word's
    /// trailing space, or the line-final pad (WordOverflowPaddingConverter). A row against the
    /// right edge ended that much short of it, a centred one sat half of it off (#269). Each row is
    /// moved, as drawn, until its glyphs meet its edge or sit centred; the move is rounded once,
    /// so a row ends within half a pixel of the edge.
    /// </summary>
    private void AlignRowsByTheirGlyphs(double width)
    {
        var rtl = RightToLeft;
        var alignment = ItemsAlignment;
        var toRight = alignment == (rtl ? WrapPanelItemsAlignment.Start : WrapPanelItemsAlignment.End);
        var toLeft = alignment == (rtl ? WrapPanelItemsAlignment.End : WrapPanelItemsAlignment.Start);
        var children = Children;
        var rowStart = 0;
        while (rowStart < children.Count)
        {
            var y = children[rowStart].Bounds.Y;
            var rowEnd = rowStart;
            double left = double.MaxValue, right = double.MinValue;
            while (rowEnd < children.Count && children[rowEnd].Bounds.Y == y)
            {
                var cell = children[rowEnd++];
                if (!cell.IsVisible || cell.Bounds.Width <= 0) continue;
                var (l, r) = GlyphsOf(cell);
                left = Math.Min(left, l);
                right = Math.Max(right, r);
            }
            if (right > left)
            {
                var shift = toRight ? width - right : toLeft ? -left : width / 2 - (left + right) / 2;
                if (Math.Abs(shift) > 0.01)
                    for (var i = rowStart; i < rowEnd; i++)
                    {
                        var child = children[i];
                        var b = child.Bounds;
                        var m = child.Margin;
                        child.Arrange(new Rect(b.X - m.Left + shift, b.Y - m.Top, b.Width + m.Left + m.Right, b.Height + m.Top + m.Bottom));
                    }
            }
            rowStart = Math.Max(rowEnd, rowStart + 1);
        }
    }

    /// <summary>Where a cell's glyphs are in this panel: its word's (the "word-sweep" layer of
    /// the word cell template), scaled as the cell scales it; the whole cell for anything else.</summary>
    private static (double Left, double Right) GlyphsOf(Control cell)
    {
        var b = cell.Bounds;
        var word = FindWord(cell);
        if (word is null || word.Bounds.Width <= 0) return (b.X, b.Right);
        var scale = b.Width / word.Bounds.Width;
        var (l, r) = word.GlyphSpan;
        return (b.X + l * scale, b.X + r * scale);
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
