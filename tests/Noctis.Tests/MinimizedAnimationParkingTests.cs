using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A minimized window keeps IsEffectivelyVisible true on everything inside it, so the
/// animated controls that only gated on visibility kept ticking — and re-rendering the
/// window — while it sat on the taskbar (Discord: "smoother minimized than maximized").
/// Each now parks on the shared <see cref="HostWindowWatch"/> signal the animated cover and
/// video backdrop already used, and resumes on restore.
/// </summary>
public class MinimizedAnimationParkingTests
{
    private static T Field<T>(object o, string name) =>
        (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;

    private static void SetField(object o, string name, object value) =>
        o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    private static void Call(object o, string name, params object?[] args) =>
        o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, args);

    private static Window Show(Control content)
    {
        var win = new Window { Width = 320, Height = 160, Content = content };
        win.Show();
        win.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return win;
    }

    private static void Frames(int n)
    {
        for (var i = 0; i < n; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ── HostWindowWatch ──

    [AvaloniaFact]
    public void Watch_FollowsTheHostWindow_AndOnlyReportsMinimizedFlips()
    {
        var owner = new Border();
        var flips = 0;
        var watch = new HostWindowWatch(owner, () => flips++);
        var win = Show(owner);
        try
        {
            Assert.Same(win, watch.Window);
            Assert.False(watch.IsMinimized);

            win.WindowState = WindowState.Maximized;
            Assert.Equal(0, flips);
            win.WindowState = WindowState.Minimized;
            Assert.True(watch.IsMinimized);
            Assert.Equal(1, flips);
            win.WindowState = WindowState.Normal;
            Assert.False(watch.IsMinimized);
            Assert.Equal(2, flips);

            win.Content = null; // detached: lets go of the window
            Assert.Null(watch.Window);
            win.WindowState = WindowState.Minimized;
            Assert.Equal(2, flips);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void Watch_OnAWindowItself_WatchesThatWindow()
    {
        var win = new Window { Width = 200, Height = 100 };
        var flips = 0;
        var watch = new HostWindowWatch(win, () => flips++);
        win.Show();
        try
        {
            Assert.Same(win, watch.Window);
            win.WindowState = WindowState.Minimized;
            Assert.True(watch.IsMinimized);
            Assert.Equal(1, flips);
        }
        finally { win.Close(); }
    }

    // ── EqVisualizer ──

    [AvaloniaFact]
    public void EqVisualizer_Minimized_StopsItsTimer_RestoreResumes()
    {
        var eq = new EqVisualizer();
        var win = Show(new StackPanel { Children = { eq } });
        try
        {
            SetField(eq, "_initialized", true); // no control template headless; the timer logic is what's under test
            eq.IsPlaying = true;
            var timer = Field<DispatcherTimer>(eq, "_animTimer");
            Assert.True(timer.IsEnabled);

            win.WindowState = WindowState.Minimized;
            Assert.False(timer.IsEnabled);

            // Pause and play again while minimized: still nothing ticks.
            eq.IsPlaying = false;
            eq.IsPlaying = true;
            Assert.False(timer.IsEnabled);

            win.WindowState = WindowState.Normal;
            Assert.True(timer.IsEnabled);
            Assert.Equal(EqVisualizer.FrameInterval, timer.Interval);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void EqVisualizer_PausedWhileMinimized_StaysStoppedOnRestore()
    {
        var eq = new EqVisualizer();
        var win = Show(new StackPanel { Children = { eq } });
        try
        {
            SetField(eq, "_initialized", true);
            eq.IsPlaying = true;
            var timer = Field<DispatcherTimer>(eq, "_animTimer");
            win.WindowState = WindowState.Minimized;
            eq.IsPlaying = false;
            win.WindowState = WindowState.Normal;
            Assert.False(timer.IsEnabled);
            Assert.False(Field<bool>(eq, "_flattening"));
        }
        finally { win.Close(); }
    }

    // ── MarqueeTextBlock ──

    [AvaloniaFact]
    public void Marquee_Minimized_StopsTheLapAtTheStart_RestoreRearmsIt()
    {
        var saved = MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled;
        MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = true;
        var marquee = new MarqueeTextBlock
        {
            Text = "I Forgot That You Exist (Taylor's Version) — Extended Edition",
            FontSize = 14, MaxDisplayWidth = 120, IsMiniPlayer = true,
        };
        var win = Show(new Border { Width = 200, Height = 60, Child = marquee });
        try
        {
            var transform = Field<TranslateTransform>(marquee, "_transform");
            Call(marquee, "StartScrolling");
            Assert.True(Field<bool>(marquee, "_isRunning"));
            Frames(1);
            Thread.Sleep(30);
            Frames(1);
            Assert.True(transform.X < 0);

            win.WindowState = WindowState.Minimized;
            Assert.False(Field<bool>(marquee, "_isRunning"));
            Assert.Equal(0, transform.X);
            Frames(3);
            Assert.Equal(0, transform.X);

            // The rest-pause timer firing while minimized starts nothing.
            Call(marquee, "StartScrolling");
            Assert.False(Field<bool>(marquee, "_isRunning"));

            var generation = Field<int>(marquee, "_lapGeneration");
            win.WindowState = WindowState.Normal;
            Dispatcher.UIThread.RunJobs();
            // Restore re-measured and scheduled the next lap after the usual rest…
            Assert.True(Field<int>(marquee, "_lapGeneration") > generation + 1);
            // …and a lap can run again.
            Call(marquee, "StartScrolling");
            Assert.True(Field<bool>(marquee, "_isRunning"));
        }
        finally
        {
            win.Close();
            MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = saved;
        }
    }

    // ── SpectrumVisualizer ──

    [AvaloniaFact]
    public void Spectrum_Minimized_StopsItsFrameLoop_RestoreResumes()
    {
        var viz = new SpectrumVisualizer { IsActive = true, Width = 200, Height = 80 };
        var win = Show(viz);
        try
        {
            Frames(2);
            Assert.True(viz.IsRunning);

            win.WindowState = WindowState.Minimized;
            Frames(3);
            Assert.False(viz.IsRunning);
            Assert.Null(Field<DispatcherTimer?>(viz, "_visibilityPoll"));

            win.WindowState = WindowState.Normal;
            Assert.True(viz.IsRunning);
            Frames(3);
            Assert.True(viz.IsRunning);
        }
        finally { win.Close(); }
    }

    // ── KawarpBackground ──

    [AvaloniaFact]
    public void Kawarp_Minimized_StopsItsFrameLoop_RestoreResumes()
    {
        var kawarp = new KawarpBackground { IsActive = true, Width = 200, Height = 80 };
        var win = Show(kawarp);
        try
        {
            Frames(2);
            Assert.True(Field<bool>(kawarp, "_running"));

            win.WindowState = WindowState.Minimized;
            Frames(3);
            Assert.False(Field<bool>(kawarp, "_running"));
            Frames(3);
            Assert.False(Field<bool>(kawarp, "_running"));

            win.WindowState = WindowState.Normal;
            Frames(3);
            Assert.True(Field<bool>(kawarp, "_running"));
        }
        finally { win.Close(); }
    }

    // ── FlowingArtworkAnimator ──

    private static (Grid Host, FlowingArtworkAnimator Flow, Border Layer1) Flow(Control? hostOverride = null)
    {
        var backdrop = new Grid();
        var layer1 = new Border();
        var layer2 = new Border();
        var glow = new Border();
        backdrop.Children.Add(layer1);
        backdrop.Children.Add(layer2);
        backdrop.Children.Add(glow);
        var host = new Grid { Width = 200, Height = 120, Children = { backdrop } };
        var flow = new FlowingArtworkAnimator(hostOverride ?? host, backdrop, layer1, layer2, glow,
            () => new BeatContext(0, 0, false));
        return (host, flow, layer1);
    }

    private static double Rotation(Border layer) =>
        ((TransformGroup)layer.RenderTransform!).Children.OfType<RotateTransform>().Single().Angle;

    [AvaloniaFact]
    public async Task Flow_Minimized_StopsItsFrameLoop_RestoreResumes()
    {
        var (host, flow, layer1) = Flow();
        var win = Show(host);
        try
        {
            flow.Enabled = true;
            Frames(2);
            Assert.True(flow.IsRunning);

            win.WindowState = WindowState.Minimized;
            Frames(2);
            Assert.False(flow.IsRunning);
            Assert.Null(Field<DispatcherTimer?>(flow, "_visibilityPoll"));
            var parked = Rotation(layer1);
            await Task.Delay(50);
            Frames(3);
            Assert.Equal(parked, Rotation(layer1));

            win.WindowState = WindowState.Normal;
            Assert.True(flow.IsRunning);
            await Task.Delay(50);
            Frames(3);
            Assert.NotEqual(parked, Rotation(layer1));
        }
        finally
        {
            flow.Dispose();
            win.Close();
        }
    }

    [AvaloniaFact]
    public void Flow_HostedByTheWindowItself_ParksWhenThatWindowMinimizes()
    {
        // The mini player hands its own Window to the animator as the host.
        var win = new Window { Width = 200, Height = 120 };
        var (host, flow, _) = Flow(win);
        win.Content = host;
        win.Show();
        try
        {
            flow.Enabled = true;
            Frames(2);
            Assert.True(flow.IsRunning);

            win.WindowState = WindowState.Minimized;
            Frames(2);
            Assert.False(flow.IsRunning);

            win.WindowState = WindowState.Normal;
            Frames(2);
            Assert.True(flow.IsRunning);
        }
        finally
        {
            flow.Dispose();
            win.Close();
        }
    }
}
