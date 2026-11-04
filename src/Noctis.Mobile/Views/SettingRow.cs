using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Mobile.Views;

/// <summary>
/// A Settings row's grid (label, then a grey value). Before the grid lays out, the value gives up
/// width so the label's longest word fits on one line: a wrapping TextBlock breaks a word that
/// does not fit anywhere, which split Russian "История прослушиваний" mid-word beside
/// "42 мин за неделю" (11-04). The value trims with an ellipsis instead, as Apple's rows do.
/// Styled as Grid (Grid.setting-row keeps matching).
/// </summary>
public class SettingRow : Grid
{
    private const double ValueCap = 160, ValueFloor = 48, Gap = 8;

    protected override Type StyleKeyOverride => typeof(Grid);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width)) FitValue(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }

    private void FitValue(double width)
    {
        TextBlock? label = null, value = null;
        foreach (var child in Children)
        {
            if (child is TextBlock t && t.Classes.Contains("row-label")) label = t;
            else if (child is TextBlock v && v.Classes.Contains("row-value")) value = v;
        }
        if (label == null || value == null || !value.IsVisible || string.IsNullOrEmpty(label.Text)) return;

        double fixedWidth = 0;
        foreach (var child in Children)
        {
            if (child == label || child == value || !child.IsVisible) continue;
            child.Measure(Size.Infinity);
            fixedWidth += child.DesiredSize.Width;
        }
        var room = width - fixedWidth - LongestWord(label) - label.Margin.Left - label.Margin.Right - Gap;
        value.MaxWidth = Math.Clamp(room, ValueFloor, ValueCap);
    }

    private static double LongestWord(TextBlock label)
    {
        var typeface = new Typeface(label.FontFamily, label.FontStyle, label.FontWeight);
        double longest = 0;
        foreach (var word in label.Text!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = new FormattedText(word, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, typeface, label.FontSize, null);
            longest = Math.Max(longest, text.WidthIncludingTrailingWhitespace);
        }
        return Math.Ceiling(longest);
    }
}
