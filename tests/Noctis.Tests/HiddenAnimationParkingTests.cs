using System.ComponentModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Frame loops and timers that stayed hot for controls an ANCESTOR hides. Avalonia keeps
/// such controls attached (the mini player hides its inactive forms, the playback bar
/// hides its track box on the lyrics page, the Settings overlay and the idle island
/// toggle IsVisible), and each control only checked its own IsVisible, so they kept
/// pumping frames for pixels nobody could see.
/// </summary>
public class HiddenAnimationParkingTests
{
    private static T Field<T>(object o, string name) =>
        (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;

    private static void SetField(object o, string name, object value) =>
        o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    private static void Call(object o, string name, params object?[] args) =>
        o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, args);

    private static (Window win, Border host) Host(Control child, bool hostVisible)
    {
        var host = new Border { Width = 200, Height = 60, IsVisible = hostVisible, Child = child };
        var win = new Window { Width = 260, Height = 120, Content = host };
        win.Show();
        win.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (win, host);
    }

    // ── GlassPanel ──

    // The glass panel no longer pumps frames at all (see GlassPanelIdleTests): a hidden
    // ancestor, or a visible one, leaves it with nothing scheduled.
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlassPanel_ShownOrUnderAHiddenAncestor_RepaintsNothingByItself(bool hostVisible)
    {
        var panel = new GlassPanelIdleTests.CountingGlassPanel { UseAppGlass = false, IsGlassActive = true, BlurRadius = 14 };
        var (win, _) = Host(panel, hostVisible);
        try
        {
            GlassPanelIdleTests.Ticks(5);
            var renders = panel.Renders;
            GlassPanelIdleTests.Ticks(30);
            Assert.Equal(renders, panel.Renders);
        }
        finally { win.Close(); }
    }

    // ── MarqueeTextBlock ──

    private const string LongTitle = "I Forgot That You Exist (Taylor's Version) — Extended Edition";

    [AvaloniaFact]
    public void Marquee_UnderAHiddenAncestor_DoesNotStartScrolling()
    {
        var saved = MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled;
        MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = true;
        var marquee = new MarqueeTextBlock { Text = LongTitle, FontSize = 14, MaxDisplayWidth = 120, IsMiniPlayer = true };
        var (win, _) = Host(marquee, hostVisible: false);
        try
        {
            Call(marquee, "StartScrolling");
            Assert.False(Field<bool>(marquee, "_isRunning"));
        }
        finally
        {
            win.Close();
            MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = saved;
        }
    }

    [AvaloniaFact]
    public void Marquee_HiddenMidLap_ParksAtTheStart()
    {
        var saved = MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled;
        MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = true;
        var marquee = new MarqueeTextBlock { Text = LongTitle, FontSize = 14, MaxDisplayWidth = 120, IsMiniPlayer = true };
        var (win, host) = Host(marquee, hostVisible: true);
        try
        {
            var transform = Field<TranslateTransform>(marquee, "_transform");
            // Mid-lap: running, text already moved left.
            SetField(marquee, "_isRunning", true);
            SetField(marquee, "_lastFrameTimestamp",
                System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency / 2);
            Call(marquee, "OnFrame", TimeSpan.Zero);
            Assert.True(transform.X < 0);

            host.IsVisible = false;
            SetField(marquee, "_isRunning", true);
            Call(marquee, "OnFrame", TimeSpan.Zero);
            Assert.False(Field<bool>(marquee, "_isRunning"));
            Assert.Equal(0, transform.X);
        }
        finally
        {
            win.Close();
            MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = saved;
        }
    }

    // ── EqVisualizer ──

    [AvaloniaFact]
    public void EqVisualizer_UnderAHiddenAncestor_TicksAtThePollRateWhilePlaying()
    {
        var eq = new EqVisualizer { IsPlaying = true };
        var (win, host) = Host(eq, hostVisible: false);
        try
        {
            Call(eq, "StartAnimating");
            var timer = Field<DispatcherTimer>(eq, "_animTimer");
            Call(eq, "OnAnimTick", null, EventArgs.Empty);
            Assert.True(timer.IsEnabled);
            Assert.Equal(EqVisualizer.HiddenPollInterval, timer.Interval);

            host.IsVisible = true;
            Call(eq, "OnAnimTick", null, EventArgs.Empty);
            Assert.True(timer.IsEnabled);
            Assert.Equal(EqVisualizer.FrameInterval, timer.Interval);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void EqVisualizer_PausedWhileHidden_StopsTheTimer()
    {
        var eq = new EqVisualizer { IsPlaying = true };
        var (win, _) = Host(eq, hostVisible: false);
        try
        {
            Call(eq, "BeginFlatten");
            var timer = Field<DispatcherTimer>(eq, "_animTimer");
            Assert.True(timer.IsEnabled);
            Call(eq, "OnAnimTick", null, EventArgs.Empty);
            Assert.False(timer.IsEnabled);
            Assert.False(Field<bool>(eq, "_flattening"));
        }
        finally { win.Close(); }
    }

    private sealed class PlayingFlag : INotifyPropertyChanged
    {
        private bool _isPlaying;
        public event PropertyChangedEventHandler? PropertyChanged;
        public bool IsPlaying
        {
            get => _isPlaying;
            set { _isPlaying = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying))); }
        }
    }

    /// <summary>A closed window keeps its DataContext, so its bindings still deliver: the
    /// Sleeve form's bars (bound to Player.IsPlaying) used to restart their 16 ms timer on
    /// the next play, and the running timer rooted the whole closed mini player window.</summary>
    [AvaloniaFact]
    public void EqVisualizer_OnAClosedWindow_DoesNotRestartItsTimer()
    {
        var flag = new PlayingFlag();
        var eq = new EqVisualizer();
        eq.Bind(EqVisualizer.IsPlayingProperty, new Binding(nameof(PlayingFlag.IsPlaying)));
        var win = new Window { DataContext = flag, Content = new StackPanel { Children = { eq } } };
        win.Show();
        SetField(eq, "_initialized", true); // no control template headless; the timer logic is what's under test
        flag.IsPlaying = true;
        var timer = Field<DispatcherTimer>(eq, "_animTimer");
        Assert.True(timer.IsEnabled);

        flag.IsPlaying = false;
        win.Close();
        Assert.False(timer.IsEnabled);

        flag.IsPlaying = true;
        Assert.True(eq.IsPlaying); // the binding really does still reach the closed window
        Assert.False(timer.IsEnabled);
    }
}
