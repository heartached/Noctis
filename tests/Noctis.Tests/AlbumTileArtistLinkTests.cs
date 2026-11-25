using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Albums page tiles (11-23 feedback): the artist in the caption ("Bonobo, Jordan Rakei ·
/// 2019 · 12 songs") was plain text. Each name now opens its artist page, hit-tested on the
/// caption itself (no extra control per tile); the rest of the caption still opens the album.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class AlbumTileArtistLinkTests
{
    public AlbumTileArtistLinkTests() => ArtistCredit.ResetToDefaults();

    private const string Credit = "Bonobo, Jordan Rakei";
    private const string Caption = Credit + " · 2019 · 12 songs";

    private static (Window Win, HighlightTextBlock Text, List<string> Artists, List<string> Tiles) Mount()
    {
        var artists = new List<string>();
        var tiles = new List<string>();
        var text = new HighlightTextBlock { DisplayText = Caption, FontSize = 12, FontFamily = FontFamily.Default };
        CaptionCreditLink.SetCredit(text, Credit);
        CaptionCreditLink.SetCommand(text, new RelayCommand<string>(a => artists.Add(a!)));
        var tile = new Button
        {
            Width = 400,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Content = text,
            Command = new RelayCommand(() => tiles.Add("album")),
        };
        var win = new Window { Width = 600, Height = 100, Content = tile };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        return (win, text, artists, tiles);
    }

    private static Point CharCentre(TextBlock run, int index, Visual to)
    {
        var a = run.TextLayout.HitTestTextPosition(index);
        var b = run.TextLayout.HitTestTextPosition(index + 1);
        return run.TranslatePoint(new Point((a.X + b.X) / 2, run.Bounds.Height / 2), to)!.Value;
    }

    private static void Click(Window win, Point p, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        win.MouseMove(p, modifiers);
        win.MouseDown(p, MouseButton.Left, modifiers);
        win.MouseUp(p, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Artist_names_open_their_artist_and_the_rest_opens_the_album()
    {
        var (win, text, artists, tiles) = Mount();
        try
        {
            Click(win, CharCentre(text, Caption.IndexOf("Jordan", StringComparison.Ordinal) + 2, win));
            Click(win, CharCentre(text, 2, win));
            Assert.Equal(new[] { "Jordan Rakei", "Bonobo" }, artists);
            Assert.Empty(tiles);

            // The year, the separator between the names, and Ctrl (multi-select) stay the tile's.
            Click(win, CharCentre(text, Caption.IndexOf("2019", StringComparison.Ordinal) + 1, win));
            Click(win, CharCentre(text, "Bonobo".Length, win));
            Click(win, CharCentre(text, 2, win), RawInputModifiers.Control);
            Assert.Equal(3, tiles.Count);
            Assert.Equal(2, artists.Count);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public void Hover_boxes_only_the_name_under_the_pointer()
    {
        var (win, text, _, _) = Mount();
        try
        {
            var jordan = Caption.IndexOf("Jordan", StringComparison.Ordinal);
            win.MouseMove(CharCentre(text, jordan + 2, win));
            Dispatcher.UIThread.RunJobs();
            var box = text.Background is DrawingBrush { Drawing: DrawingGroup g }
                && g.Children[1] is GeometryDrawing { Geometry: RectangleGeometry r } ? r.Rect : (Rect?)null;
            Assert.NotNull(box);
            Assert.Equal(text.TextLayout.HitTestTextPosition(jordan).X, box!.Value.Left, 0);
            Assert.Equal(text.TextLayout.HitTestTextPosition(Credit.Length).X, box.Value.Right, 0);

            win.MouseMove(CharCentre(text, Caption.IndexOf("2019", StringComparison.Ordinal), win));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(Brushes.Transparent, text.Background);
        }
        finally
        {
            win.Close();
        }
    }
}
