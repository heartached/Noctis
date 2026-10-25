using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Home's track pills (Morning rotation, Heavy rotation, Recently rediscovered) show the
/// chart rows' hover Play / Pause over the art (user ask 10-25), and hover in the Home card
/// colour like the chart rows.
/// </summary>
public class HomeHistoryPillPlayOverlayTests
{
    private static Track Song(string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = "Nine Inch Nails",
        AlbumArtist = "Nine Inch Nails",
        Album = "Pretty Hate Machine",
        FilePath = TestPaths.Primary("pill-overlay", $"{Guid.NewGuid():N}.mp3"),
        Duration = TimeSpan.FromMinutes(4),
    };

    private static (HomeViewModel Vm, PlayerViewModel Player) Make()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        return (new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib)), player);
    }

    [AvaloniaFact]
    public void TogglePlay_PlaysFromItsRow_ThenPausesTheSameTrack()
    {
        var (vm, player) = Make();
        var a = Song("Head Like A Hole");
        var b = Song("Terrible Lie");
        vm.RediscoveredTracks.Add(a);
        vm.RediscoveredTracks.Add(b);

        vm.TogglePlayRediscoveredCommand.Execute(b);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(b, player.CurrentTrack);
        Assert.True(b.IsNowPlaying);

        vm.TogglePlayRediscoveredCommand.Execute(b);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(b, player.CurrentTrack);
        Assert.False(player.IsPlaying);
    }

    [AvaloniaFact]
    public async Task EveryPill_HasArtPlayOverlay_AndHoversInHomeCardColour()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var (vm, _) = Make();
        vm.TimeRotationTracks.Add(Song("a"));
        vm.HeavyRotationTracks.Add(Song("b"));
        vm.RediscoveredTracks.Add(Song("c"));

        var view = new HomeView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        try
        {
            List<Button> Pills() => view.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("home-history-pill")).ToList();
            var deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline && Pills().Count < 3)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5);
            }
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var pills = Pills();
            Assert.Equal(3, pills.Count);
            var expected = new System.Windows.Input.ICommand[] { vm.TogglePlayTimeRotationCommand, vm.TogglePlayHeavyRotationCommand, vm.TogglePlayRediscoveredCommand };
            Assert.Equal(expected, pills.Select(p => p.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Classes.Contains("art-play-overlay")).Command));

            var pill = pills[0];
            var overlay = pill.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("art-play-overlay"));
            Assert.Equal(0, overlay.Opacity);
            ((IPseudoClasses)pill.Classes).Set(":pointerover", true);
            // The fade-in is a 0.12 s transition; wait it out.
            var fadeDeadline = Environment.TickCount64 + 2000;
            while (Environment.TickCount64 < fadeDeadline && overlay.Opacity < 1)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
            Assert.Equal(1, overlay.Opacity, 3);

            var presenter = pill.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            Assert.True(view.TryFindResource("HomeCardHoverBackground", view.ActualThemeVariant, out var hover));
            Assert.Equal(((ISolidColorBrush)hover!).Color, ((ISolidColorBrush)presenter.Background!).Color);
        }
        finally
        {
            window.Close();
        }
    }
}
