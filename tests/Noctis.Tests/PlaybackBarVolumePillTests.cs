using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The playback bar's volume pop-up (10-08 redesign): a filled glass pill with no outline, a
/// live % readout, the level glyph on the speaker button, click-to-mute, wheel / keyboard
/// stepping, and the reveal / hide that turns around mid-flight instead of dipping.
/// </summary>
public class PlaybackBarVolumePillTests
{
    private static void EnsureIcons()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("SpeakerHighIcon", null, out _)) return;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
    }

    private sealed record Rig(Window Window, PlaybackBarView Bar, PlayerViewModel Vm)
    {
        public Popup Flyout => Bar.FindControl<Popup>("VolumeFlyout")!;
        public Button Button => Bar.FindControl<Button>("VolumeButton")!;
        public GlassPanel Content => Bar.FindControl<GlassPanel>("VolumeFlyoutContent")!;
        public Grid Pill => Bar.FindControl<Grid>("VolumePill")!;
        public TextBlock Readout => Bar.FindControl<TextBlock>("VolumeReadout")!;
        public void Click() => Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        /// <summary>Pointer over the speaker button, so the posted hover check never arms the
        /// 140 ms close under a slow test run.</summary>
        public Point Hover()
        {
            var at = Button.TranslatePoint(new Point(Button.Bounds.Width / 2, Button.Bounds.Height / 2), Window)!.Value;
            Window.MouseMove(at);
            Dispatcher.UIThread.RunJobs();
            return at;
        }
    }

    private static Rig Build(int volume = 50)
    {
        EnsureIcons();
        var vm = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.Volume = volume;
        var bar = new PlaybackBarView { DataContext = vm };
        var window = new Window { Width = 900, Height = 300, Content = bar };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new Rig(window, bar, vm);
    }

    /// <summary>Muted dims the level in the dimmed (disabled-slider) layout: the see-through
    /// thumb must not sit over the fill or the rest track.</summary>
    private static void AssertMutedLayout(Rig rig, bool muted)
    {
        var fill = rig.Bar.FindControl<Border>("VolumeTrackFill")!;
        var rest = rig.Bar.FindControl<Border>("VolumeTrackBackground")!;
        var thumb = rig.Bar.FindControl<Panel>("VolumeThumb")!;
        var thumbLeft = ((TranslateTransform)thumb.RenderTransform!).X;
        if (muted)
        {
            Assert.Equal(0.45, fill.Opacity);
            Assert.Equal(0.45, thumb.Opacity);
            Assert.True(Canvas.GetLeft(fill) + fill.Width <= thumbLeft + 1.5, "fill runs under the dimmed thumb");
            Assert.True(Canvas.GetLeft(rest) >= thumbLeft + thumb.Width - 1.5, "rest track runs under the dimmed thumb");
        }
        else
        {
            Assert.Equal(1, fill.Opacity);
            Assert.Equal(1, thumb.Opacity);
        }
    }

    private static double BaseFade(GlassPanel p) => p.GetBaseValue(GlassPanel.FadeProperty).GetValueOrDefault();
    private static double BaseOpacity(Visual v) => v.GetBaseValue(Visual.OpacityProperty).GetValueOrDefault();

    [AvaloniaFact]
    public void Pill_IsAFilledGlassSurface_WithNoOutline()
    {
        var rig = Build();
        // The old capsule was a Border with a 1.5px TextControlBorderBrush ring.
        Assert.IsType<GlassPanel>(rig.Content);
        Assert.DoesNotContain(rig.Content.GetLogicalDescendants().OfType<Border>(),
            b => b.BorderThickness != default && b.BorderBrush != null);
        Assert.Equal(new CornerRadius(999), rig.Content.CornerRadius);
        Assert.NotEqual(0, rig.Content.BoxShadow.Count);
        // Liquid Glass tint follows Settings → Glass Opacity like the island and its menus.
        rig.Vm.IslandBackgroundOpacity = 0.63;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0.63, rig.Content.GlassTintOpacity);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Readout_FollowsEveryVolumeWriter()
    {
        var rig = Build(volume: 42);
        Assert.Equal("42%", rig.Readout.Text);
        // Outside writers (shortcuts, mini player, Local API, MPRIS) set the VM directly.
        rig.Vm.Volume = 7;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("7%", rig.Readout.Text);
        rig.Vm.Volume = 100;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("100%", rig.Readout.Text);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void SpeakerGlyph_TracksTheLevel_AndMute()
    {
        var rig = Build();
        Geometry Icon(string key) => (Geometry)Application.Current!.FindResource(key)!;
        ShapePath Shown() => rig.Button.GetLogicalDescendants().OfType<Viewbox>().Single(v => v.IsVisible)
            .GetLogicalDescendants().OfType<ShapePath>().Single();

        foreach (var (volume, key) in new[] { (0, "SpeakerZeroIcon"), (20, "SpeakerLowIcon"), (80, "SpeakerHighIcon") })
        {
            rig.Vm.Volume = volume;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(Icon(key), Shown().Data);
        }
        rig.Vm.IsMuted = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(Icon("SpeakerMuteIcon"), Shown().Data);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void FirstClickOpens_SecondMutes_AndUnmuteKeepsTheLevel()
    {
        var rig = Build(volume: 64);
        rig.Hover();
        rig.Click();
        Dispatcher.UIThread.RunJobs();
        Assert.True(rig.Flyout.IsOpen);
        Assert.False(rig.Vm.IsMuted);
        Assert.Equal(1, BaseFade(rig.Content));
        Assert.Equal(1, BaseOpacity(rig.Pill));

        rig.Click();
        Dispatcher.UIThread.RunJobs();
        Assert.True(rig.Vm.IsMuted);
        Assert.Contains("muted", rig.Pill.Classes);
        Assert.Equal(64, rig.Vm.Volume);
        AssertMutedLayout(rig, muted: true);

        rig.Click();
        Dispatcher.UIThread.RunJobs();
        Assert.False(rig.Vm.IsMuted);
        Assert.DoesNotContain("muted", rig.Pill.Classes);
        Assert.Equal(64, rig.Vm.Volume);
        Assert.Equal("64%", rig.Readout.Text);
        AssertMutedLayout(rig, muted: false);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void ReopenDuringExitFade_TurnsAroundWithoutDipping()
    {
        var rig = Build();
        rig.Hover();
        rig.Click();
        Dispatcher.UIThread.RunJobs();

        // Escape on the focused button closes: hidden pose, still mapped for the exit fade.
        rig.Button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Assert.True(rig.Bar.IsVolumeFlyoutExiting);
        Assert.True(rig.Flyout.IsOpen);
        Assert.Equal(0, BaseFade(rig.Content));

        // Reopen mid-exit: the target goes straight back to shown; it is never re-set to
        // hidden first (the old dip), and the posted reveal cannot be undone by the close.
        rig.Click();
        Assert.False(rig.Bar.IsVolumeFlyoutExiting);
        Assert.Equal(1, BaseFade(rig.Content));
        Assert.Equal(1, BaseOpacity(rig.Pill));
        Dispatcher.UIThread.RunJobs();
        Assert.True(rig.Flyout.IsOpen);
        Assert.Equal(1, BaseFade(rig.Content));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void ArrowKeysOnTheButton_StepVolume_AndKeepTheKeyboardPopupOpen()
    {
        var rig = Build(volume: 50);
        rig.Vm.IsMuted = true;
        Assert.True(rig.Button.Focus(NavigationMethod.Tab));
        Dispatcher.UIThread.RunJobs();

        var up = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up };
        rig.Button.RaiseEvent(up);
        Assert.True(up.Handled);
        Assert.Equal(55, rig.Vm.Volume);
        Assert.False(rig.Vm.IsMuted); // adjusting unmutes, as the wheel and the drag do
        Dispatcher.UIThread.RunJobs();
        Assert.True(rig.Flyout.IsOpen);
        // The pointer is nowhere near: the old code armed the hover-close here (~140 ms).
        Assert.False(rig.Bar.IsVolumeFlyoutCloseArmed);

        rig.Button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left });
        Assert.Equal(50, rig.Vm.Volume);
        rig.Vm.Volume = 98;
        rig.Button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
        Assert.Equal(100, rig.Vm.Volume); // clamped

        // Ctrl+arrows belong to the global shortcuts, not the button.
        var ctrl = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, KeyModifiers = KeyModifiers.Control };
        rig.Button.RaiseEvent(ctrl);
        Assert.False(ctrl.Handled);
        Assert.Equal(100, rig.Vm.Volume);

        // Focus leaving arms the normal close.
        var other = rig.Bar.GetLogicalDescendants().OfType<Button>()
            .First(b => !ReferenceEquals(b, rig.Button) && b.IsEffectivelyVisible && b.Focusable);
        Assert.True(other.Focus(NavigationMethod.Tab));
        Dispatcher.UIThread.RunJobs();
        Assert.True(rig.Bar.IsVolumeFlyoutCloseArmed);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void MouseWheelNotch_StepsFive_AndClamps()
    {
        var rig = Build(volume: 50);
        var center = rig.Hover();
        rig.Window.MouseWheel(center, new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(55, rig.Vm.Volume);
        Assert.True(rig.Flyout.IsOpen);
        rig.Window.MouseWheel(center, new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(50, rig.Vm.Volume);
        rig.Window.Close();
    }

    [Fact]
    public void WheelAccumulator_FractionalDeltasNoLongerRaceToTheLimit()
    {
        var wheel = new PlaybackBarView.VolumeWheelAccumulator();
        // Mouse notches: 1.0 each → one step each.
        Assert.Equal(1, wheel.Add(1, 1000));
        Assert.Equal(1, wheel.Add(1, 1050));
        Assert.Equal(-1, wheel.Add(-1, 1100)); // reversal steps at once

        // Touchpad / hi-res wheel: ten 0.2 deltas in one gesture used to be ten ±5 steps (50%).
        wheel = new PlaybackBarView.VolumeWheelAccumulator();
        var steps = 0;
        for (var i = 0; i < 10; i++) steps += wheel.Add(0.2, 5000 + (ulong)i * 10);
        Assert.Equal(2, steps); // the gesture's free first step, then one per whole notch

        // A lone slow fractional tick after a pause still moves.
        Assert.Equal(1, wheel.Add(0.3, 9000));
        // An accelerated burst moves at most two steps.
        Assert.Equal(-2, wheel.Add(-4, 9010));
    }

    /// <summary>Real-Skia probe for inspection only (NOCTIS_TEST_SKIA=1): saves the open pill.</summary>
    [AvaloniaFact]
    public void Probe_SavesTheOpenPill()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("HeartFillIcon", null, out _))
        {
            app.Resources["InterSemiBold"] = FontFamily.Default;
            app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
        }
        var rig = Build(volume: 62);
        rig.Window.Background = new SolidColorBrush(Color.Parse("#2A3550"));
        rig.Hover();
        rig.Click();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 600)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "volume-shots");
        Directory.CreateDirectory(dir);
        rig.Window.CaptureRenderedFrame()!.Save(System.IO.Path.Combine(dir, "volume-pill-open.png"));
        rig.Vm.IsMuted = true;
        sw.Restart();
        while (sw.ElapsedMilliseconds < 400)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        rig.Window.CaptureRenderedFrame()!.Save(System.IO.Path.Combine(dir, "volume-pill-muted.png"));
        rig.Window.Close();
    }
}
