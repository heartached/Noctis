using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Mobile.Services;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>The phone's frame stall monitor (adb shell setprop debug.noctis.stalls 1) says what ran
/// through a stall and, every window, the allocation rate and how long each reported state was on,
/// so a stall the user saw (while the lyrics glide) tells apart from one on a still screen.</summary>
public class MobileFrameStallMonitorTests
{
    [AvaloniaFact]
    public void Stalls_SayWhatRanThroughThem_AndTheSummaryCountsTheSeenOnes()
    {
        // Something changes on every frame (the headless loop sends no frame for a still window).
        var box = new Border { Width = 10, Height = 10 };
        var window = new Window { Width = 100, Height = 100, Content = box };
        window.Show();
        var every = FrameStallMonitor.SummaryEvery;
        FrameStallMonitor.SummaryEvery = TimeSpan.FromMilliseconds(400);
        DebugLog.Clear();
        void Frame(int sleepMs)
        {
            Thread.Sleep(sleepMs);
            box.Background = box.Background == Avalonia.Media.Brushes.Red ? Avalonia.Media.Brushes.Blue : Avalonia.Media.Brushes.Red;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
        try
        {
            FrameStallMonitor.Start(window);
            Frame(5);
            FrameStallMonitor.SetState(FrameStallMonitor.MotionState, true);
            Frame(80);                                       // a stall while the lines glide
            FrameStallMonitor.SetState(FrameStallMonitor.MotionState, false);
            Frame(5);
            Frame(80);                                       // a stall on a still screen
            Frame(5);
            for (var i = 0; i < 40 && !DebugLog.Snapshot().Contains("stalls in"); i++) Frame(20);

            var lines = DebugLog.Snapshot().Split('\n').Where(l => l.Contains("[Stalls]")).ToList();
            var stalls = lines.Where(l => l.Contains("] stall ")).ToList();
            // (The first frame after Start may itself be slow: only the last two are the test's.)
            Assert.True(stalls.Count >= 2, string.Join(" | ", lines));
            Assert.Contains("during lyric glide", stalls[^2]);
            Assert.DoesNotContain("during", stalls[^1]);
            var summary = Assert.Single(lines, l => l.Contains("stalls in"));
            Assert.Contains("stalls while lyric glide 1,", summary);
            Assert.Contains("lyric glide on 0.", summary);
            Assert.Matches(@"alloc \d+ KB/s \(UI thread \d+, other threads \d+\), GC0 \+", summary);
        }
        finally
        {
            FrameStallMonitor.Stop();
            FrameStallMonitor.SummaryEvery = every;
            window.Close();
        }
    }
}
