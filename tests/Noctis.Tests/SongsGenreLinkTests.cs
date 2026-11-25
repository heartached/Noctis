using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Songs page Genre cell (11-23 feedback): it was painted as a link (accent-link) but did
/// nothing. Each genre of "Rock · Pop" now opens its own genre page, like the album page's
/// genre links, through the Artist cell's per-name link (ArtistCreditLink with a separator).
/// </summary>
[Collection("ArtistCredit global configuration")]
public class SongsGenreLinkTests
{
    public SongsGenreLinkTests() => ArtistCredit.ResetToDefaults();

    [Fact]
    public void Locate_with_a_separator_splits_on_it_only()
    {
        var spans = ArtistCreditSpans.Locate("Hip-Hop/Rap · R&B, Soul", " · ");

        Assert.Equal(2, spans.Count);
        Assert.Equal(new ArtistCreditSpans.Span(0, 11, "Hip-Hop/Rap"), spans[0]);
        Assert.Equal(new ArtistCreditSpans.Span(14, 9, "R&B, Soul"), spans[1]);
        Assert.Null(ArtistCreditSpans.NameAt("Rock · Pop", 5, " · "));
        Assert.Equal("Pop", ArtistCreditSpans.NameAt("Rock · Pop", 8, " · "));
    }

    private static (Window Win, Button Button, TextBlock Text, List<string> Opened) Mount(string genres)
    {
        var opened = new List<string>();
        var text = new TextBlock { Text = genres, FontSize = 12, FontFamily = FontFamily.Default };
        var button = new Button
        {
            Background = Brushes.Transparent, BorderThickness = default, Padding = default, MinHeight = 0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Content = text,
            Command = new RelayCommand<string>(g => opened.Add(g!)),
            CommandParameter = genres,
        };
        ArtistCreditLink.SetIsEnabled(button, true);
        ArtistCreditLink.SetSeparator(button, " · ");
        var win = new Window { Width = 600, Height = 100, Content = button };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        return (win, button, text, opened);
    }

    private static Point CharCentre(TextBlock run, int index, Visual to)
    {
        var a = run.TextLayout.HitTestTextPosition(index);
        var b = run.TextLayout.HitTestTextPosition(index + 1);
        return run.TranslatePoint(new Point((a.X + b.X) / 2, run.Bounds.Height / 2), to)!.Value;
    }

    private static void Click(Window win, Point p)
    {
        win.MouseMove(p);
        win.MouseDown(p, MouseButton.Left);
        win.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void A_click_on_one_genre_opens_that_genre()
    {
        const string genres = "Rock · Pop";
        var (win, _, text, opened) = Mount(genres);
        try
        {
            Click(win, CharCentre(text, genres.IndexOf("Pop", StringComparison.Ordinal) + 1, win));
            Click(win, CharCentre(text, 1, win));
            Assert.Equal(new[] { "Pop", "Rock" }, opened);
        }
        finally
        {
            win.Close();
        }
    }

    [Fact]
    public void Songs_OpenGenre_opens_the_genre_and_the_whole_line_opens_its_first()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        var opened = new List<string>();
        vm.SetOpenGenreAction(opened.Add);

        vm.OpenGenreCommand.Execute("Pop");
        vm.OpenGenreCommand.Execute("Rock · Pop");
        vm.OpenGenreCommand.Execute("");

        Assert.Equal(new[] { "Pop", "Rock" }, opened);
    }
}
