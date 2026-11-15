using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The marquee used to scroll the text fully out on the left and only then bring it back
/// in from the right, leaving the viewport blank in between. It is a ticker now: a copy of
/// the text follows one gap behind, so something is always in view, and the lap ends the
/// moment the copy stands where the original started (user ask 09-17).
/// </summary>
public class MarqueeLoopTests : IDisposable
{
    private readonly bool _saved = MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled;
    public void Dispose() => MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = _saved;

    private static T Field<T>(MarqueeTextBlock m, string name) =>
        (T)typeof(MarqueeTextBlock).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(m)!;

    private static void SetField(MarqueeTextBlock m, string name, object value) =>
        typeof(MarqueeTextBlock).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(m, value);

    private static void OnFrame(MarqueeTextBlock m) =>
        typeof(MarqueeTextBlock).GetMethod("OnFrame", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(m, new object[] { TimeSpan.Zero });

    /// <summary>Advances the lap by <paramref name="seconds"/> of scroll time in one frame.</summary>
    private static void Advance(MarqueeTextBlock m, double seconds)
    {
        SetField(m, "_isRunning", true);
        SetField(m, "_lastFrameTimestamp", System.Diagnostics.Stopwatch.GetTimestamp() - (long)(System.Diagnostics.Stopwatch.Frequency * seconds));
        OnFrame(m);
    }

    private static (Window win, MarqueeTextBlock marquee) Mount(string text, double width)
    {
        MarqueeTextBlock.GlobalMiniPlayerTitleScrollEnabled = true;
        var marquee = new MarqueeTextBlock { Text = text, FontSize = 14, MaxDisplayWidth = width, IsMiniPlayer = true };
        var host = new Border { Width = width, Height = 40, Child = marquee };
        var win = new Window { Width = width + 40, Height = 80, Content = host };
        win.Show();
        win.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (win, marquee);
    }

    [AvaloniaFact]
    public void OverflowingTitle_ShowsItsLoopCopyOneGapBehind()
    {
        var (win, m) = Mount("I Forgot That You Exist (Taylor's Version)", 160);
        try
        {
            var original = Field<TextBlock>(m, "_textBlock");
            var copy = Field<TextBlock>(m, "_loopCopy");
            Assert.True(copy.IsVisible);
            Assert.Equal(original.Text, copy.Text);
            win.UpdateLayout();
            Assert.Equal(original.Bounds.Right + MarqueeTextBlock.LoopGap, copy.Bounds.X, 1.0);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void ShortTitle_HidesTheLoopCopy()
    {
        var (win, m) = Mount("Hi", 160);
        try
        {
            Assert.False(Field<TextBlock>(m, "_loopCopy").IsVisible);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void MidLap_TheViewportIsNeverBlank()
    {
        var (win, m) = Mount("I Forgot That You Exist (Taylor's Version)", 160);
        try
        {
            var textWidth = Field<double>(m, "_textWidth");
            var viewportWidth = Field<double>(m, "_viewportWidth");
            var copy = Field<TextBlock>(m, "_loopCopy");
            var transform = Field<Avalonia.Media.TranslateTransform>(m, "_transform");
            win.UpdateLayout();

            // Step the lap in 0.1s slices and check that at every step some text covers the
            // viewport: the original's tail is still inside, or the copy's head already is.
            var lap = textWidth + MarqueeTextBlock.LoopGap;
            for (var t = 0.1; ; t += 0.1)
            {
                Advance(m, 0.1);
                var x = transform.X;
                if (x == 0 && t > 0.2) break; // landed: lap over, rests at the start
                var tailInside = x + textWidth > 0;
                var copyHeadInside = x + copy.Bounds.X < viewportWidth;
                Assert.True(tailInside || copyHeadInside,
                    $"blank viewport at X={x:F1} (text {textWidth:F0}, viewport {viewportWidth:F0}, copy at {copy.Bounds.X:F0})");
                Assert.True(t < 60, "lap never landed");
            }
            Assert.True(lap > viewportWidth, "test needs a lap longer than the viewport");
        }
        finally { win.Close(); }
    }

    /// <summary>The playback bar has its own marquee (title + artist, shared frame clock);
    /// its step is the same ticker: travel left one lap, snap to the start, rest.</summary>
    [Fact]
    public void PlaybackBar_TickMarquee_TravelsOneLapThenSnapsToStartAndRests()
    {
        const double lap = 200;
        var offset = 0.0;
        var pause = 0.0;
        var positions = new List<double>();
        void Set(double o) { offset = o; positions.Add(o); }

        // 30 px/s: 100 ms frames move 3 px; the lap must be crossed monotonically, never
        // jumping to the right (the old model re-entered from the right edge).
        for (var i = 0; i < 70; i++)
            Noctis.Views.PlaybackBarView.TickMarquee(100, offset, ref pause, lap, Set);
        Assert.True(positions.Take(66).Zip(positions.Skip(1).Take(65)).All(p => p.Second < p.First),
            "the outbound pass must only ever move left");
        Assert.Contains(0, positions.Skip(60));
        Assert.True(pause > 0, "resting after the lap");
        // During the rest the offset holds at the start.
        Noctis.Views.PlaybackBarView.TickMarquee(100, offset, ref pause, lap, Set);
        Assert.Equal(0, offset);
    }

    [AvaloniaFact]
    public void Lap_EndsWhenTheCopyReachesTheStart_AndRests()
    {
        var (win, m) = Mount("I Forgot That You Exist (Taylor's Version)", 160);
        try
        {
            var lap = Field<double>(m, "_lapDistance");
            var transform = Field<Avalonia.Media.TranslateTransform>(m, "_transform");
            Assert.Equal(Field<double>(m, "_textWidth") + MarqueeTextBlock.LoopGap, lap, 0.01);

            // Just short of a lap (frames are clamped to 0.1s, so step): still travelling.
            while (transform.X - 3 > -lap)
                Advance(m, 0.1);
            Assert.True(transform.X < 0 && transform.X > -lap, $"X={transform.X}");
            Assert.True(Field<bool>(m, "_isRunning"));

            // Past the lap distance: snapped to the start and resting (not running).
            Advance(m, 0.1);
            Assert.Equal(0, transform.X);
            Assert.False(Field<bool>(m, "_isRunning"));
        }
        finally { win.Close(); }
    }

    /// <summary>A scrolling title waits out its 7 s rest on a DispatcherTimer, which the dispatcher
    /// roots along with its callback. Taken off screen (a closed mini player window), the control
    /// and everything above it stayed alive until that rest ran out: the timer is stopped now.</summary>
    [AvaloniaFact]
    public void OffScreenMarquee_IsNotHeldByItsRestTimer()
    {
        var weak = MountAndDetach();
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference MountAndDetach()
    {
        var (win, m) = Mount("I Forgot That You Exist (Taylor's Version)", 160);
        Assert.NotNull(Field<object?>(m, "_lapTimer")); // resting before its first lap
        ((Border)win.Content!).Child = null;
        win.Close();
        Dispatcher.UIThread.RunJobs();
        return new WeakReference(m);
    }
}
