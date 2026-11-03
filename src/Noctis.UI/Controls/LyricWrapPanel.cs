using Avalonia;
using Avalonia.Controls;

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
        if (!RightToLeft) return size;
        var width = finalSize.Width;
        foreach (var child in Children)
        {
            // Bounds is the arranged box inside the margin: mirror the whole slot.
            var b = child.Bounds;
            var m = child.Margin;
            var x = b.X - m.Left;
            var w = b.Width + m.Left + m.Right;
            child.Arrange(new Rect(width - x - w, b.Y - m.Top, w, b.Height + m.Top + m.Bottom));
        }
        return size;
    }
}
