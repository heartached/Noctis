using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord 11-23: the island's seek line was too hard to hit — its hit strip ended 2.5px under
/// the 3px line (the card's rounded clip cut it there). The strip now reaches down to the
/// island's own bottom edge and still 10px+ above the line, while the art, title and artist
/// keep their clicks and nothing outside the island is taken.
/// </summary>
public class PlaybackBarSeekHitStripTests
{
    private static Control? HitAt(Window win, Visual relativeTo, Point local)
    {
        var p = relativeTo.TranslatePoint(local, win)!.Value;
        return win.InputHitTest(p) as Control;
    }

    private static bool IsInside(Control? hit, Control target)
        => hit != null && (ReferenceEquals(hit, target) || hit.GetVisualAncestors().Contains(target));

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static (Window win, PlaybackBarView bar) Show()
    {
        EnsureAppStyles();
        // A song from a library album, so the title (View Album) and artist links are enabled
        // (disabled controls take no clicks).
        var library = new FakeLibraryService();
        var album = new Noctis.Models.Album { Id = Guid.NewGuid(), Name = "Album", Artist = "Artist" };
        ((List<Noctis.Models.Album>)library.Albums).Add(album);
        var player = new PlayerViewModel(new FakeAudioPlayer(), library,
            new TestPersistenceService(), new FakeAnimatedCoverService());
        player.CurrentTrack = new Noctis.Models.Track
        {
            Id = Guid.NewGuid(), Title = "A Song With A Title", Artist = "Artist", Album = "Album", AlbumId = album.Id,
            FilePath = TestPaths.Primary("Music", "Album", "01.mp3"), Duration = TimeSpan.FromMinutes(3),
        };
        player.ViewCurrentTrackAlbumCommand.NotifyCanExecuteChanged();
        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 1200, Height = 200, Content = bar };
        win.Show();
        bar.FindControl<Grid>("TrackInfoPanel")!.IsVisible = true;
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        // Theme brushes the headless app doesn't load: without a fill these aren't hit-testable.
        bar.FindControl<Border>("AlbumArtThumb")!.Background = Avalonia.Media.Brushes.Gray;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (win, bar);
    }

    private static string Describe(Control? c) =>
        c == null ? "nothing" : string.Join(" < ", new[] { c }.Concat(c.GetVisualAncestors().OfType<Control>()).Take(6)
            .Select(v => $"{v.GetType().Name}#{v.Name}{v.Bounds}"));

    /// <summary>The drawn line's centre, in the seek row (the row is 6px, the line 3px at y 2).</summary>
    private const double LineY = 3.5;

    [AvaloniaFact]
    public void PressesAboveAndBelowTheLine_HitTheSeekSlider()
    {
        var (win, bar) = Show();
        try
        {
            var row = bar.FindControl<Grid>("SeekRow")!;
            var slider = bar.FindControl<Slider>("SeekSlider")!;
            var island = bar.FindControl<Border>("IslandBorder")!;
            var midX = row.Bounds.Width / 2;

            foreach (var dy in new[] { -11.0, -10, -8, -4, 0, 3, 5 })
            {
                var hit = HitAt(win, row, new Point(midX, LineY + dy));
                Assert.True(IsInside(hit, slider), $"dy={dy}: {Describe(hit)}");
            }

            // Below the line the strip ends inside the island: the page under the floating
            // island keeps its clicks.
            var islandBottom = island.TranslatePoint(new Point(0, island.Bounds.Height), row)!.Value.Y;
            Assert.False(IsInside(HitAt(win, row, new Point(midX, islandBottom + 1)), slider));
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void ArtTitleAndArtist_KeepTheirClicks()
    {
        var (win, bar) = Show();
        try
        {
            var art = bar.FindControl<Border>("AlbumArtThumb")!;
            Assert.True(IsInside(HitAt(win, art, new Point(art.Bounds.Width / 2, art.Bounds.Height / 2)), art));

            var titleButton = bar.FindControl<Border>("TrackTitleViewport")!.GetVisualDescendants().OfType<Button>().First();
            var artistButton = bar.FindControl<Button>("ArtistNameButton")!;
            foreach (var button in new[] { titleButton, artistButton })
            {
                var b = button.Bounds;
                var hit = HitAt(win, button, new Point(Math.Min(20, b.Width / 2), b.Height / 2));
                Assert.True(IsInside(hit, button), $"{button.Name ?? "title"} {b}: {Describe(hit)}");
            }
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void PressUnderTheLine_Seeks()
    {
        var (win, bar) = Show();
        try
        {
            var row = bar.FindControl<Grid>("SeekRow")!;
            var slider = bar.FindControl<Slider>("SeekSlider")!;
            var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, LineY + 5), win)!.Value;

            win.MouseDown(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var pressed = slider.Value;
            win.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            // The press under the line started a seek to the middle of the song.
            Assert.InRange(pressed, 0.4, 0.6);
        }
        finally { win.Close(); }
    }
}
