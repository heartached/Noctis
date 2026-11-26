using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26: the sidebar's hover / peek / pin motion felt choppy. SidebarMotion moves the
/// rail with the content held still, drops the expanded layout only once the rail is narrow,
/// and slides the page with a render transform on pin / unpin.
/// </summary>
public class SidebarMotionTests
{
    private sealed class Rig
    {
        public required Window Host;
        public required Border Rail;
        public required Border Content;
        public required Border Page;
        public required SidebarMotion Motion;
    }

    /// <summary>MainWindow's shape: the page under a left margin, the rail overlaid on it.</summary>
    private static Rig Build()
    {
        var content = new Border { Background = Brushes.Gray };
        var rail = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 60, Margin = new Thickness(8, 8, 8, 12), Child = content };
        var page = new Border
        {
            Background = Brushes.Black,
            Margin = new Thickness(76, 0, 0, 0),
            RenderTransform = new TranslateTransform
            {
                Transitions = new Transitions { new DoubleTransition { Property = TranslateTransform.XProperty, Duration = TimeSpan.FromMilliseconds(200) } },
            },
        };
        var host = new Window { Width = 1200, Height = 700, Content = new Panel { Children = { page, rail } } };
        var motion = new SidebarMotion(rail, content);
        host.Show();
        Dispatcher.UIThread.RunJobs();
        return new Rig { Host = host, Rail = rail, Content = content, Page = page, Motion = motion };
    }

    private static async Task Wait(TimeSpan t)
    {
        await Task.Delay(t + TimeSpan.FromMilliseconds(80));
        for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Motion_UsesOneEaseOutCurve_OpenSlowerThanClose()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(240), SidebarMotion.OpenDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(200), SidebarMotion.CloseDuration);
        var ease = Assert.IsType<CubicBezierEase>(SidebarMotion.Ease);
        // Ease-out: well past half way at the half-time mark, and lands exactly.
        Assert.True(ease.Ease(0.5) > 0.8, $"{ease.Ease(0.5)}");
        Assert.Equal(1, ease.Ease(1), 3);
    }

    [AvaloniaFact]
    public async Task HoverOpen_HoldsTheContent_AndLeavesThePageWidthAlone()
    {
        var r = Build();
        var pageWidth = r.Page.Bounds.Width;

        r.Motion.MoveTo(220);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(220, r.Motion.TargetWidth);
        // The content is laid out once at the final width, not every frame of the move.
        Assert.Equal(220, r.Content.Width);
        Assert.Equal(HorizontalAlignment.Left, r.Content.HorizontalAlignment);
        Assert.Equal(pageWidth, r.Page.Bounds.Width); // overlaid: the page never re-flows

        await Wait(SidebarMotion.OpenDuration);
        Assert.True(double.IsNaN(r.Content.Width), "the content is released once the rail got there");
        Assert.Equal(HorizontalAlignment.Stretch, r.Content.HorizontalAlignment);
        Assert.Equal(220, r.Rail.Width);
        Assert.Equal(pageWidth, r.Page.Bounds.Width);
        r.Host.Close();
    }

    /// <summary>The expanded layout (labels, left-aligned icons) must stay until the rail is
    /// narrow: dropping it on the first frame re-centred the icons in the still-wide rail.</summary>
    [AvaloniaFact]
    public async Task Collapse_SettlesOnlyOnceTheRailIsNarrow()
    {
        var r = Build();
        r.Motion.Snap(220);
        var settled = 0;
        r.Motion.MoveTo(60, () => settled++);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, settled);
        Assert.Equal(220, r.Content.Width); // held at the wide width while it shrinks

        await Wait(SidebarMotion.CloseDuration);
        Assert.Equal(1, settled);
        Assert.Equal(60, r.Rail.Width);
        r.Host.Close();
    }

    [AvaloniaFact]
    public async Task ReversedMove_CancelsTheEarlierSettle()
    {
        var r = Build();
        r.Motion.Snap(220);
        var collapsed = false;
        r.Motion.MoveTo(60, () => collapsed = true);
        r.Motion.MoveTo(220); // the pointer came back
        await Wait(SidebarMotion.CloseDuration);
        Assert.False(collapsed, "a stale collapse must not drop the expanded layout");
        Assert.Equal(220, r.Motion.TargetWidth);
        r.Host.Close();
    }

    [AvaloniaFact]
    public void Snap_LandsAtOnce_WithoutAnimating()
    {
        var r = Build();
        r.Motion.Snap(0);
        Assert.Equal(0, r.Rail.Width);
        Assert.True(double.IsNaN(r.Content.Width));
        Assert.NotNull(r.Rail.Transitions); // the motion stays attached for the next move
        r.Host.Close();
    }

    /// <summary>Hide sidebar until hover: the peeking rail slid in but vanished in one frame;
    /// with the motion it slides back out and only then leaves the tree.</summary>
    [AvaloniaFact]
    public async Task Peek_SlidesOut_BeforeHiding()
    {
        var r = Build();
        var edge = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 8, Background = Brushes.Transparent };
        ((Panel)r.Host.Content!).Children.Add(edge);
        r.Motion.Snap(0);
        r.Rail.IsVisible = false;
        var expanded = false;
        var peek = new SidebarEdgePeek(edge, r.Rail, () => true, () => true, () => false, e => expanded = e)
        {
            Motion = r.Motion,
        };
        peek.SyncHidden();
        Dispatcher.UIThread.RunJobs();

        r.Host.MouseMove(new Point(2, 300));
        Dispatcher.UIThread.RunJobs();
        Assert.True(peek.IsPeeking);
        Assert.True(r.Rail.IsVisible);
        Assert.True(expanded);
        Assert.Equal(SidebarEdgePeek.ExpandedWidth, r.Motion.TargetWidth);
        await Wait(SidebarMotion.OpenDuration);

        r.Host.MouseMove(new Point(900, 300));
        for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
        Assert.False(peek.IsPeeking);
        Assert.True(r.Rail.IsVisible, "still on screen while it slides out");
        Assert.True(expanded, "labels stay until the rail is gone");
        Assert.Equal(0, r.Motion.TargetWidth);

        await Wait(SidebarMotion.CloseDuration);
        Assert.False(r.Rail.IsVisible);
        Assert.False(expanded);
        r.Host.Close();
    }

    /// <summary>Pin / unpin: the page takes its new margin at once (one layout) and is drawn
    /// where it was, then slides there, instead of jumping while the rail still moves.</summary>
    [AvaloniaFact]
    public void Pin_SlidesThePage_FromWhereItWas()
    {
        var r = Build();
        var t = (TranslateTransform)r.Page.RenderTransform!;
        t.Transitions = null; // read the start offset, not the animation
        var slide = new DoubleTransition { Property = TranslateTransform.XProperty };

        SidebarMotion.SlidePage(r.Page, 236, opening: true);
        Assert.Equal(236, r.Page.Margin.Left);
        Assert.Equal(0, t.X); // no transition attached: lands at once

        // With the slide attached, the start offset is old - new and its curve is the rail's.
        t.Transitions = new Transitions { slide };
        var probe = new List<double>();
        t.PropertyChanged += (_, e) => { if (e.Property == TranslateTransform.XProperty) probe.Add((double)e.NewValue!); };
        SidebarMotion.SlidePage(r.Page, 76, opening: false);
        Assert.Equal(76, r.Page.Margin.Left);
        Assert.Contains(160.0, probe); // drawn where it was (236 = 76 + 160) on the first frame
        Assert.Equal(SidebarMotion.CloseDuration, slide.Duration);
        Assert.Same(SidebarMotion.Ease, slide.Easing);
        r.Host.Close();
    }
}
