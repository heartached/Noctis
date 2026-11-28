using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Noctis.Controls;

/// <summary>
/// A lyric row's text (a plain line, its translation or romanization) whose wrapped rows end
/// flush with the edge they are aligned to. Avalonia places a right-aligned row by its width
/// with its trailing space (TextLineImpl.GetParagraphOffsetX), so a wrapped left-to-right row
/// against the right edge ended one space short of it, and a right-to-left row against the left
/// edge (its trailing space is on its left) began one space in. Centred rows are already centred
/// on their visible text. Only the drawing moves; measuring is TextBlock's own.
/// </summary>
public class LyricTextBlock : TextBlock
{
    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        var y = origin.Y;
        var lines = TextLayout.TextLines;
        for (var i = 0; i < lines.Count; i++)   // indexed: drawn every frame a row moves, allocates nothing
        {
            var line = lines[i];
            line.Draw(context, new Point(origin.X + EdgeShift(line), y));
            y += line.Height;
        }
    }

    /// <summary>How far <paramref name="line"/> is drawn from where Avalonia places it, so its
    /// visible text meets the edge it is aligned to (or sits centred). A row whose trailing space
    /// overflows the box is clamped by Avalonia to the left; that is undone too.</summary>
    internal double EdgeShift(TextLine line)
    {
        var trailing = line.WidthIncludingTrailingWhitespace - line.Width;
        var width = TextLayout.MaxWidth;
        if (trailing <= 0 || double.IsInfinity(width)) return 0;
        var rtl = FlowDirection == FlowDirection.RightToLeft;
        // The visible text, as Avalonia places it: a right-to-left row's trailing space is on its left.
        var left = line.Start + (rtl ? trailing : 0);
        var right = left + line.Width;
        return Alignment(rtl) switch
        {
            TextAlignment.Right => width - right,
            TextAlignment.Center => width / 2 - (left + right) / 2,
            _ => -left,
        };
    }

    private TextAlignment Alignment(bool rtl) => TextAlignment switch
    {
        TextAlignment.Start or TextAlignment.Justify => rtl ? TextAlignment.Right : TextAlignment.Left,
        TextAlignment.End => rtl ? TextAlignment.Left : TextAlignment.Right,
        var a => a,
    };

    /// <summary>Where row <paramref name="index"/>'s visible text starts and ends as drawn, in
    /// this control's coordinates (inside its padding).</summary>
    internal (double Left, double Right) VisibleRow(int index)
    {
        var line = TextLayout.TextLines[index];
        var left = Padding.Left + line.Start + EdgeShift(line);
        if (FlowDirection == FlowDirection.RightToLeft)
            left += line.WidthIncludingTrailingWhitespace - line.Width;   // its trailing space is on its left
        return (left, left + line.Width);
    }
}
