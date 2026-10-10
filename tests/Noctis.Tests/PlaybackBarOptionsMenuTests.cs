using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-10: the player bar's "…" menu (main bar and lyrics page) takes the same layout as
/// the track menus on Home: the four quick tiles on top, View Artist per credited name, the
/// lyrics entries under Lyrics ▸, and Remove from Library last on the lyrics page too.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class PlaybackBarOptionsMenuTests
{
    public PlaybackBarOptionsMenuTests() => ArtistCredit.ResetToDefaults();

    private static (Window Win, PlaybackBarView Bar, PlayerViewModel Player, MenuFlyout Menu) Open(bool lyricsPage, bool playing = false)
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        player.CurrentTrack = new Track
        {
            Id = Guid.NewGuid(), Title = "Work", Artist = "Rihanna; Drake", Album = "ANTI",
            AlbumId = Guid.NewGuid(), Duration = TimeSpan.FromSeconds(200), FilePath = "C:/m/work.mp3",
        };
        player.IsLyricsPageActive = lyricsPage;
        player.State = playing ? PlaybackState.Playing : PlaybackState.Paused;

        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 900, Height = 600, Content = bar };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        var menu = Assert.IsType<MenuFlyout>(bar.FindControl<Button>("OptionsButton")!.Flyout);
        menu.ShowAt(bar);
        Dispatcher.UIThread.RunJobs();
        return (win, bar, player, menu);
    }

    private static IEnumerable<Control> Visible(MenuFlyout menu) =>
        menu.Items.OfType<Control>().Where(c => c.IsVisible);

    [AvaloniaFact]
    public void Opens_WithTheFourQuickTilesOnTop()
    {
        var (win, _, player, menu) = Open(lyricsPage: false);
        try
        {
            var first = Assert.IsType<MenuItem>(menu.Items[0]);
            Assert.Contains("mv2-panel", first.Classes);
            var tiles = first.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("mv2-tile")).ToList();
            Assert.Equal(4, tiles.Count);
            Assert.Same(player.PlayPauseCommand, tiles[0].Command);
            Assert.Same(player.ShuffleCurrentAlbumCommand, tiles[1].Command);
            Assert.Same(player.PlayNextCurrentTrackCommand, tiles[2].Command);
            Assert.Same(player.AddCurrentTrackToQueueCommand, tiles[3].Command);

            // A second open does not stack another tile row.
            menu.Hide();
            menu.ShowAt(win);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(menu.Items.OfType<MenuItem>(), i => i.Classes.Contains("mv2-panel"));
        }
        finally { menu.Hide(); win.Close(); }
    }

    [AvaloniaFact]
    public void PlayTile_ReadsPause_WhileASongPlays()
    {
        var (win, _, _, menu) = Open(lyricsPage: false, playing: true);
        try
        {
            var play = ((MenuItem)menu.Items[0]!).GetLogicalDescendants().OfType<Button>().First();
            Assert.Equal("Pause", Avalonia.Controls.ToolTip.GetTip(play));
        }
        finally { menu.Hide(); win.Close(); }
    }

    [AvaloniaFact]
    public void ViewArtist_ListsEachCreditedName()
    {
        var (win, bar, player, menu) = Open(lyricsPage: false);
        try
        {
            var viewArtist = bar.FindControl<MenuItem>("ViewArtistMenuItem")!;
            Assert.True(viewArtist.IsVisible);
            var names = viewArtist.Items.OfType<MenuItem>().ToList();
            Assert.Equal(new[] { "Rihanna", "Drake" }, names.Select(n => n.Header));
            Assert.All(names, n => Assert.Same(player.ViewArtistNamedCommand, n.Command));
            // Each name carries its round picture slot, as in the other track menus (owner 10-10).
            Assert.All(names, n => Assert.Contains("mv2-avatar", Assert.IsType<Border>(n.Icon).Classes));
        }
        finally { menu.Hide(); win.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoveFromLibrary_IsTheLastRow_WithNoStraySeparators(bool lyricsPage)
    {
        var (win, _, _, menu) = Open(lyricsPage);
        try
        {
            var rows = Visible(menu).ToList();
            var last = Assert.IsType<MenuItem>(rows[^1]);
            Assert.Contains("danger", last.Classes);
            Assert.IsNotType<Separator>(rows[0]);
            for (var i = 1; i < rows.Count; i++)
                Assert.False(rows[i] is Separator && rows[i - 1] is Separator, "doubled separator at " + i);
        }
        finally { menu.Hide(); win.Close(); }
    }
}
