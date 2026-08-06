using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Views;
using Xunit;
using Xunit.Abstractions;

namespace Noctis.Tests;

/// <summary>
/// Regression cover for issue #30: in the playlist track list, a long song title
/// painted straight over the Album column instead of ellipsizing at the end of its
/// own column.
///
/// Two separate defects produced that, and either one alone is enough to bring it
/// back — so both are pinned here:
///
/// 1. The title cell was a horizontal StackPanel (title + explicit badge + NEW
///    badge). A horizontal StackPanel measures its children with INFINITE available
///    width, so the title never had a width to trim against; it reported its full
///    text width and the row Grid, which does not clip, let it paint over the
///    neighbouring Album column.
///
/// 2. The title carries Classes="one-line-explicit-title", but that style's selector
///    is <c>TextBlock.one-line-explicit-title</c> and the control is a
///    <see cref="HighlightTextBlock"/>. Avalonia type selectors match the EXACT type,
///    not derived types (that is why Styles.axaml has to declare a separate
///    <c>controls|HighlightTextBlock.accent-link</c> rule), so NoWrap/MaxLines/
///    TextTrimming never reached the title at all.
/// </summary>
public class PlaylistRowTitleOverflowTests
{
    private readonly ITestOutputHelper _output;

    public PlaylistRowTitleOverflowTests(ITestOutputHelper output) => _output = output;

    // Real offenders from the issue thread plus a pathological control case.
    private const string LongTitle =
        "Bella Ciao - Música Original de la Serie la Casa de Papel/ Money Heist";
    private const string LongerTitle =
        "Tócate Tu Misma (Remix) [feat. Bad Bunny, Anonimus, Larry Over, Jonh Z & Brytiago]";

    private static List<Track> BuildTracks() => new()
    {
        new Track { Title = "Head Like A Hole", Artist = "Nine Inch Nails", Album = "Pretty Hate Machine" },
        new Track { Title = LongTitle, Artist = "Manu Pilas", Album = "Bella Ciao (Música Original de la Serie la Casa de Papel/ Money Heist)" },
        new Track { Title = LongerTitle, Artist = "Alexis y Fido", Album = "Tócate Tu Misma (Remix) [feat. Bad Bunny, Anonimus, Larry Over, Jonh Z & Brytiago] - Single", IsExplicit = true },
        new Track { Title = new string('W', 160), Artist = "Control", Album = "Control Album" },
    };

    /// <summary>Mounts the real PlaylistView and drives its track list directly, so the
    /// geometry under test is the shipped row template rather than a copy of it.
    ///
    /// Assets/Styles.axaml is deliberately NOT loaded here: HeadlessTestApp carries only
    /// the Fluent theme, and pulling the app sheet in would need its StaticResource font
    /// planted in Application.Resources, which the suite shares. The row therefore has to
    /// stand on its own — which is the point, since the class it used to lean on never
    /// applied to this control in the first place.</summary>
    private static (Window Window, ListBox List) Mount(double width)
    {
        var view = new PlaylistView();
        var window = new Window { Width = width, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();

        var list = view.FindControl<ListBox>("TrackList")!;
        list.ItemsSource = BuildTracks();

        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, list);
    }

    private static IEnumerable<(Track Track, HighlightTextBlock Title, Control Album, Control Row)> Rows(ListBox list)
    {
        foreach (var container in list.GetRealizedContainers().OfType<ListBoxItem>())
        {
            if (container.DataContext is not Track track)
                continue;

            var title = container.GetVisualDescendants().OfType<HighlightTextBlock>()
                .FirstOrDefault(t => t.DisplayText == track.TitleDisplay);
            var album = container.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Classes.Contains("album-btn"));
            var row = container.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("row-body"));

            if (title != null && album != null && row != null)
                yield return (track, title, album, row);
        }
    }

    private static double RightEdgeIn(Control child, Visual reference)
    {
        var origin = child.TranslatePoint(new Point(0, 0), reference);
        return origin == null ? double.NaN : origin.Value.X + child.Bounds.Width;
    }

    private static double LeftEdgeIn(Control child, Visual reference)
    {
        var origin = child.TranslatePoint(new Point(0, 0), reference);
        return origin == null ? double.NaN : origin.Value.X;
    }

    // ── 1. The bug itself, measured on the real row template ──

    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(1100)]
    [InlineData(1400)]
    public void LongTitle_NeverPaintsIntoTheAlbumColumn(double windowWidth)
    {
        var (window, list) = Mount(windowWidth);
        try
        {
            var checkedRows = 0;
            var worstOverlap = 0.0;
            var offender = "";

            foreach (var (track, title, album, row) in Rows(list))
            {
                var titleRight = RightEdgeIn(title, row);
                var albumLeft = LeftEdgeIn(album, row);
                if (double.IsNaN(titleRight) || double.IsNaN(albumLeft))
                    continue;

                checkedRows++;
                var overlap = titleRight - albumLeft;
                _output.WriteLine(
                    $"w={windowWidth} titleRight={titleRight:F1} albumLeft={albumLeft:F1} " +
                    $"overlap={overlap:F1}px  \"{track.Title}\"");

                if (overlap > worstOverlap)
                {
                    worstOverlap = overlap;
                    offender = track.Title;
                }
            }

            Assert.True(checkedRows > 0, "no playlist rows were realized — harness broken");
            Assert.True(worstOverlap <= 0.5,
                $"title overflowed {worstOverlap:F1}px into the Album column at window width " +
                $"{windowWidth}: \"{offender}\"");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The visible symptom: an overlong title must end in an ellipsis rather
    /// than render its full text. Guards against a "fix" that only clips the cell.</summary>
    [AvaloniaFact]
    public void LongTitle_IsTrimmedRatherThanRenderedFull()
    {
        var (window, list) = Mount(1100);
        try
        {
            var rows = Rows(list).ToList();
            Assert.NotEmpty(rows);

            foreach (var (track, title, _, _) in rows)
            {
                Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
                Assert.Equal(TextWrapping.NoWrap, title.TextWrapping);
                _output.WriteLine($"trimming={title.TextTrimming} w={title.Bounds.Width:F1} \"{track.Title}\"");
            }
        }
        finally
        {
            window.Close();
        }
    }

    // ── 2. Why the class alone does not fix it (Avalonia exact-type selectors) ──

    /// <summary>
    /// Documents defect #2 so nobody "fixes" a future recurrence by re-adding the
    /// class and calling it done. A <c>TextBlock.foo</c> selector does not match a
    /// subclass of TextBlock; the properties must be set inline (as LibrarySongsView
    /// already does) or the style must target <c>controls|HighlightTextBlock</c>.
    /// </summary>
    [AvaloniaFact]
    public void TextBlockTypeSelector_DoesNotReachHighlightTextBlock()
    {
        var plain = new TextBlock { Text = "plain" };
        plain.Classes.Add("one-line-explicit-title");

        var highlight = new HighlightTextBlock { DisplayText = "highlight" };
        highlight.Classes.Add("one-line-explicit-title");

        var panel = new StackPanel { Children = { plain, highlight } };
        var window = new Window { Width = 400, Height = 200, Content = panel };

        // The exact rule from Assets/Styles.axaml.
        var style = new Style(x => x.OfType<TextBlock>().Class("one-line-explicit-title"));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        window.Styles.Add(style);

        window.Show();
        window.UpdateLayout();
        try
        {
            _output.WriteLine($"TextBlock={plain.TextTrimming} HighlightTextBlock={highlight.TextTrimming}");
            Assert.Equal(TextTrimming.CharacterEllipsis, plain.TextTrimming);
            Assert.NotEqual(TextTrimming.CharacterEllipsis, highlight.TextTrimming);
        }
        finally
        {
            window.Close();
        }
    }
}
