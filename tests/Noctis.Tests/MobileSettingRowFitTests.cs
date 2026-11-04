using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>11-04 emulator, Russian: "История прослушиваний" broke mid-word ("прослушиван / ий")
/// beside "42 мин за неделю". A Settings row now trims the value so the label's words stay whole.</summary>
public class MobileSettingRowFitTests
{
    private const string Label = "История прослушиваний";
    private const string Value = "42 мин за неделю";

    private static (TextBlock Label, TextBlock Value) Layout(Grid row, double width)
    {
        row.ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto");
        row.Margin = new Thickness(16, 0);
        var chip = new Border { Width = 32, Height = 32, Margin = new Thickness(0, 0, 14, 0) };
        var label = new TextBlock { Text = Label, FontSize = 17, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        label.Classes.Add("row-label");
        var value = new TextBlock { Text = Value, FontSize = 17, MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 0, 0) };
        value.Classes.Add("row-value");
        var chevron = new Border { Width = 20, Height = 20, Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(label, 1); Grid.SetColumn(value, 2); Grid.SetColumn(chevron, 3);
        row.Children.AddRange(new Control[] { chip, label, value, chevron });
        var window = new Window { Width = width, Height = 200, Content = row };
        window.Show();
        window.UpdateLayout();
        window.Close();
        return (label, value);
    }

    private static bool SplitsAWord(TextBlock label)
    {
        var lines = label.TextLayout.TextLines;
        var text = label.Text!;
        foreach (var line in lines.Take(lines.Count - 1))
        {
            var end = line.FirstTextSourceIndex + line.Length;
            if (end <= 0 || end >= text.Length) continue;
            if (!char.IsWhiteSpace(text[end - 1]) && !char.IsWhiteSpace(text[end])) return true;
        }
        return false;
    }

    [AvaloniaFact]
    public void APlainGrid_SplitsTheWord_TheSettingRowKeepsItWhole()
    {
        // The headless font draws Cyrillic wider than the phone's, so size the row from the word:
        // room for "прослушиваний" plus a 100 dp value, less than the 160 dp a plain grid gives it.
        var word = new FormattedText("прослушиваний", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 17, null).WidthIncludingTrailingWhitespace;
        var width = Math.Ceiling(word) + 32 + 46 + 26 + 8 + 8 + 100;
        var (plainLabel, _) = Layout(new Grid(), width);
        Assert.True(SplitsAWord(plainLabel), "the plain grid no longer reproduces the 11-04 split");

        var (label, value) = Layout(new SettingRow(), width);
        Assert.False(SplitsAWord(label), $"maxW={value.MaxWidth} valW={value.Bounds.Width} labW={label.Bounds.Width} lines={string.Join("|", label.TextLayout.TextLines.Select(l => label.Text!.Substring(l.FirstTextSourceIndex, Math.Min(l.Length, label.Text.Length - l.FirstTextSourceIndex))))}");
        Assert.True(value.MaxWidth < 160);
        Assert.True(value.MaxWidth >= 48);
    }

    [AvaloniaFact]
    public void AShortLabel_LeavesTheValueItsFullWidth()
    {
        var row = new SettingRow();
        var (label, value) = Layout(row, 2000);
        Assert.Equal(160, value.MaxWidth);
    }
}
