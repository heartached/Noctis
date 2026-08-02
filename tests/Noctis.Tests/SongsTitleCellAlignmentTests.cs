using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Xunit;
using Xunit.Abstractions;

namespace Noctis.Tests;

/// <summary>
/// The Songs row's title cell packs three things into one Grid: the 36px artwork
/// thumb, the title, and the explicit badge. The thumb and the badge both set
/// VerticalAlignment=Center (the badge via the shared Border.explicit-badge style),
/// so the cell is 36px tall and those two are centred in it. A TextBlock left at the
/// default VerticalAlignment=Stretch is arranged over the full 36px and draws its
/// text at the TOP of that box — so the title rides high against the artwork and
/// sits above the badge it is supposed to sit beside.
///
/// These tests measure the real layout: the title's text centre must line up with
/// both the artwork centre and the badge centre.
/// </summary>
public class SongsTitleCellAlignmentTests
{
    private readonly ITestOutputHelper _output;

    public SongsTitleCellAlignmentTests(ITestOutputHelper output) => _output = output;

    private const double ArtSize = 36;

    /// <summary>
    /// Rebuilds the LibrarySongsView title cell (LibrarySongsView.axaml:121-170).
    /// <paramref name="centreTitle"/> toggles the one property under test.
    /// </summary>
    private static (Window Window, Grid Cell, Border Art, TextBlock Title, Border Badge) Mount(bool centreTitle)
    {
        var art = new Border
        {
            Width = ArtSize,
            Height = ArtSize,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            [Grid.ColumnProperty] = 0,
        };
        art.Classes.Add("row-art");

        var title = new TextBlock
        {
            Text = "PARANOID",
            FontSize = 13,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            [Grid.ColumnProperty] = 1,
        };
        if (centreTitle)
            title.VerticalAlignment = VerticalAlignment.Center;

        // Mirrors Border.explicit-badge / .compact in Styles.axaml:265-275.
        var badge = new Border
        {
            Height = 14,
            Padding = new Thickness(4, 0),
            Margin = new Thickness(6, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "E", FontSize = 9 },
            [Grid.ColumnProperty] = 2,
        };
        badge.Classes.Add("explicit-badge");

        var cell = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        cell.Children.Add(art);
        cell.Children.Add(title);
        cell.Children.Add(badge);

        // Row host tall enough that the cell's own centring is not what's under test.
        var row = new Grid { Height = 46, Children = { cell } };
        var window = new Window { Width = 900, Height = 200, Content = row };
        window.Show();
        window.UpdateLayout();

        return (window, cell, art, title, badge);
    }

    private static double CentreY(Control c, Visual relativeTo)
    {
        var origin = c.TranslatePoint(new Point(0, 0), relativeTo);
        Assert.NotNull(origin);
        return origin!.Value.Y + c.Bounds.Height / 2;
    }

    /// <summary>
    /// A stretched TextBlock is ARRANGED over the whole 36px cell but still draws its
    /// glyphs at the top of that box (Avalonia has no vertical content alignment on
    /// TextBlock). So the box centre lines up fine while the visible text does not —
    /// measuring the box would hide the bug. What matters is the overshoot: how much
    /// taller the arranged box is than the text it paints.
    /// </summary>
    [AvaloniaFact]
    public void DefaultStretch_AnchorsTitleTextToTopOfCell()
    {
        var (window, cell, art, title, badge) = Mount(centreTitle: false);

        var textHeight = title.DesiredSize.Height;
        var boxHeight = title.Bounds.Height;
        // Glyphs sit at the top of the box, so the text centre is half a text-height
        // down from the box top rather than at the box centre.
        var textCentre = title.TranslatePoint(new Point(0, 0), window)!.Value.Y + textHeight / 2;
        var artCentre = CentreY(art, window);

        _output.WriteLine(
            $"box={boxHeight:F1} text={textHeight:F1} overshoot={boxHeight - textHeight:F1} " +
            $"| textCentre={textCentre:F1} artCentre={artCentre:F1} " +
            $"badgeCentre={CentreY(badge, window):F1} cellHeight={cell.Bounds.Height:F1}");

        Assert.True(boxHeight - textHeight > 1.0,
            "expected the stretched title box to overshoot its text; if this is now zero " +
            "the defect was fixed — tighten this test.");
        Assert.True(Math.Abs(textCentre - artCentre) > 1.0,
            $"expected the drawn text ({textCentre:F1}) to sit above the artwork centre ({artCentre:F1})");
    }

    [AvaloniaFact]
    public void CentredTitle_LinesUpWithArtworkAndBadge()
    {
        var (window, _, art, title, badge) = Mount(centreTitle: true);

        var artCentre = CentreY(art, window);
        var titleCentre = CentreY(title, window);
        var badgeCentre = CentreY(badge, window);

        _output.WriteLine(
            $"art={artCentre:F1} title={titleCentre:F1} badge={badgeCentre:F1} " +
            $"| box={title.Bounds.Height:F1} text={title.DesiredSize.Height:F1}");

        // Centred: the box collapses to the text, so box centre == text centre.
        Assert.True(Math.Abs(title.Bounds.Height - title.DesiredSize.Height) <= 0.5,
            "the centred title box should hug its text so the glyphs are actually centred");
        Assert.True(Math.Abs(titleCentre - artCentre) <= 0.5,
            $"title centre {titleCentre:F1} should match artwork centre {artCentre:F1}");
        Assert.True(Math.Abs(titleCentre - badgeCentre) <= 0.5,
            $"title centre {titleCentre:F1} should match badge centre {badgeCentre:F1}");
    }
}
