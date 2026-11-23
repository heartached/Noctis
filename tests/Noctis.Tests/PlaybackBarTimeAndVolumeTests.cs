using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord 11-23, Player Island: a click on the time swaps its lower line between the time left
/// and the total length (remembered, no width jump); the volume pop-up's "100%" readout fits.
/// </summary>
public class PlaybackBarTimeAndVolumeTests
{
    private static PlayerViewModel MakePlayer() => new(
        new FakeAudioPlayer(), new FakeLibraryService(),
        new TestPersistenceService(), new FakeAnimatedCoverService());

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Settle(Window win)
    {
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void ToggleIslandTimeDisplay_FlipsBetweenRemainingAndTotal()
    {
        var player = MakePlayer();
        Assert.False(player.IslandTimeShowsTotal);

        player.ToggleIslandTimeDisplayCommand.Execute(null);
        Assert.True(player.IslandTimeShowsTotal);

        player.ToggleIslandTimeDisplayCommand.Execute(null);
        Assert.False(player.IslandTimeShowsTotal);
    }

    [Fact]
    public void TimeShowsTotal_DefaultsToTimeLeft()
    {
        Assert.False(new Noctis.Models.AppSettings().PlaybackBarTimeShowsTotal);
    }

    [AvaloniaFact]
    public void ClickingTheTime_SwapsTheLowerLine_WithoutChangingItsWidth()
    {
        EnsureAppStyles();
        var player = MakePlayer();
        player.IslandShowTime = true;
        player.PositionText = "1:04";
        player.RemainingTimeText = "2:56";
        player.DurationText = "4:00";
        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 1200, Height = 200, Content = bar };
        try
        {
            win.Show();
            bar.FindControl<Grid>("TrackInfoPanel")!.IsVisible = true;
            Settle(win);

            var button = bar.FindControl<Button>("TrackTimeButton")!;
            var remaining = bar.FindControl<TextBlock>("TrackTimeRemaining")!;
            var total = bar.FindControl<TextBlock>("TrackTimeTotal")!;
            Assert.Equal("-2:56", remaining.Text);
            Assert.Equal("4:00", total.Text);
            Assert.False(button.Classes.Contains("total"));
            var width = button.Bounds.Width;

            button.Command!.Execute(null);
            Settle(win);

            Assert.True(player.IslandTimeShowsTotal);
            Assert.True(button.Classes.Contains("total"));
            // Both lines stay laid out (they only crossfade), so the column never jumps.
            Assert.Equal(width, button.Bounds.Width);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void VolumeReadout_FitsEveryLevel_AndThePillNeverResizes()
    {
        EnsureAppStyles();
        var player = MakePlayer();
        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 1200, Height = 300, Content = bar };
        try
        {
            win.Show();
            Settle(win);
            bar.FindControl<Popup>("VolumeFlyout")!.IsOpen = true;
            Settle(win);

            var readout = bar.FindControl<TextBlock>("VolumeReadout")!;
            var pill = bar.FindControl<Grid>("VolumePill")!;
            var slider = bar.FindControl<Slider>("VolumeSlider")!;
            double? pillWidth = null, sliderX = null;
            foreach (var level in new[] { 0, 5, 42, 99, 100 })
            {
                player.Volume = level;
                Settle(win);
                Assert.Equal($"{level}%", readout.Text);

                var probe = new TextBlock
                {
                    Text = readout.Text, FontFamily = readout.FontFamily, FontSize = readout.FontSize,
                    FontWeight = readout.FontWeight, FontFeatures = readout.FontFeatures,
                };
                probe.Measure(Size.Infinity);
                Assert.True(probe.DesiredSize.Width <= readout.Bounds.Width,
                    $"{readout.Text}: needs {probe.DesiredSize.Width}, has {readout.Bounds.Width}");

                pillWidth ??= pill.Bounds.Width;
                sliderX ??= slider.TranslatePoint(default, pill)!.Value.X;
                Assert.Equal(pillWidth, pill.Bounds.Width);
                Assert.Equal(sliderX, slider.TranslatePoint(default, pill)!.Value.X);
            }
        }
        finally { win.Close(); }
    }
}
