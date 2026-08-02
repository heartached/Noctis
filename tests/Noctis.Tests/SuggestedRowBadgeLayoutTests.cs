using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Xunit;
using Xunit.Abstractions;

namespace Noctis.Tests;

/// <summary>
/// The SUGGESTED rail rows carry an explicit badge that must sit hard against the end
/// of the title (like the Songs rows) rather than parked at the far edge of the row.
/// That means Auto/Auto columns instead of star/Auto — which reintroduces the risk the
/// star layout avoided: a long title can push the badge out of the fixed-width rail.
/// The title's MaxWidth is what prevents that, so these tests pin the real geometry.
/// </summary>
public class SuggestedRowBadgeLayoutTests
{
    private readonly ITestOutputHelper _output;

    public SuggestedRowBadgeLayoutTests(ITestOutputHelper output) => _output = output;

    // PlaylistView left rail: 320px column, StackPanel Margin="24,20,16,115" => 280,
    // item Grid Margin="4,3" => 272, columns 44 (art) + * + 24 (add button),
    // inner StackPanel Margin="8,0,8,0" => 188 available for title + badge.
    private const double AvailableWidth = 188;
    private const double TitleMaxWidth = 150;

    private static (Grid Row, TextBlock Title, Border Badge) Mount(string title)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 12,
            MaxWidth = TitleMaxWidth,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            [Grid.ColumnProperty] = 0,
        };

        var badge = new Border
        {
            Height = 14,
            Padding = new Thickness(4, 0),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "E", FontSize = 9 },
            [Grid.ColumnProperty] = 1,
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = AvailableWidth,
        };
        row.Children.Add(titleBlock);
        row.Children.Add(badge);

        var host = new Grid { Width = AvailableWidth, Children = { row } };
        var window = new Window { Width = 400, Height = 200, Content = host };
        window.Show();
        window.UpdateLayout();

        return (row, titleBlock, badge);
    }

    [AvaloniaTheory]
    [InlineData("Style")]
    [InlineData("mad woman")]
    [InlineData("The Smallest Man Who Ever Lived (Taylor's Version) [From The Vault]")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Badge_SitsBesideTitle_AndStaysInsideTheRail(string title)
    {
        var (row, titleBlock, badge) = Mount(title);

        var titleRight = titleBlock.Bounds.Right;
        var badgeLeft = badge.Bounds.Left;
        var badgeRight = badge.Bounds.Right;
        var gap = badgeLeft - titleRight;

        _output.WriteLine(
            $"\"{(title.Length > 28 ? title[..28] + "…" : title)}\" " +
            $"titleWidth={titleBlock.Bounds.Width:F1} titleRight={titleRight:F1} " +
            $"badge=[{badgeLeft:F1}..{badgeRight:F1}] gap={gap:F1} rowWidth={row.Bounds.Width:F1}");

        // Adjacent: the badge's 6px left margin (+<=1px of text-layout rounding once the
        // title is trimmed at its cap), never parked at the far edge of the rail — which
        // is what the previous star/Auto layout produced.
        Assert.InRange(gap, 5.5, 7.5);

        // Never pushed out of the rail by a long title.
        Assert.True(badgeRight <= AvailableWidth + 0.5,
            $"badge right edge {badgeRight:F1} overflows the {AvailableWidth}px rail");

        Assert.True(titleBlock.Bounds.Width <= TitleMaxWidth + 0.5,
            $"title width {titleBlock.Bounds.Width:F1} exceeded its {TitleMaxWidth}px cap");
    }
}
