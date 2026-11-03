using System.Windows.Input;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The transport buttons' Apple Music press (TransportGlyph): circle + dip + spring on press,
/// the skip glyph's conveyor on Previous / Next, the Play ↔ Pause morph. Frames are driven on a
/// fixed 60 fps clock (TransportGlyph.ManualFrames), so every value is read at an exact time.
/// </summary>
public sealed class TransportGlyphMotionTests : IDisposable
{
    private const double Fps = 60;

    public TransportGlyphMotionTests() => TransportGlyph.ManualFrames = true;

    public void Dispose() => TransportGlyph.ManualFrames = false;

    private sealed class CountingCommand : ICommand
    {
        public int Count { get; private set; }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => Count++;
    }

    private sealed class Rig
    {
        public required Window Window { get; init; }
        public required Button Button { get; init; }
        public required TransportGlyph Glyph { get; init; }
        public required CountingCommand Command { get; init; }
        public double Now { get; set; } = 10;

        /// <summary>One 60 fps frame.</summary>
        public void Frame()
        {
            Now += 1 / Fps;
            Glyph.OnFrame(Now);
        }

        public void Run(double seconds)
        {
            for (var i = 0; i < Math.Round(seconds * Fps); i++) Frame();
        }

        /// <summary>Frames until nothing moves (at most 3 s).</summary>
        public void Settle()
        {
            for (var i = 0; i < 3 * Fps && Glyph.IsMoving; i++) Frame();
            Assert.False(Glyph.IsMoving, "still moving after 3 s");
        }

        private Point Centre => Button.TranslatePoint(new Point(Button.Bounds.Width / 2, Button.Bounds.Height / 2), Window)!.Value;
        public void Down() => Window.MouseDown(Centre, MouseButton.Left);
        public void Up() => Window.MouseUp(Centre, MouseButton.Left);
    }

    private static Rig Open(TransportKind kind, bool playing = false)
    {
        var command = new CountingCommand();
        var glyph = new TransportGlyph { Kind = kind, Width = 18, Height = 18, Foreground = Brushes.White, IsPlaying = playing };
        var button = new Button { Width = 34, Height = 34, Padding = default, Command = command, Content = glyph };
        AutomationPropertiesHelper(button, kind);
        var window = new Window { Width = 200, Height = 120, Background = Brushes.Black, Content = button };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();   // the glyph counts as settled once the bind is done
        return new Rig { Window = window, Button = button, Glyph = glyph, Command = command };
    }

    private static void AutomationPropertiesHelper(Button button, TransportKind kind) =>
        Avalonia.Automation.AutomationProperties.SetName(button, kind.ToString());

    [AvaloniaFact]
    public void Press_ShowsTheCircleAndDipsTheGlyph_ReleaseSpringsBackToExactRest()
    {
        var rig = Open(TransportKind.Next);
        rig.Down();
        rig.Run(0.2);
        Assert.InRange(rig.Glyph.HighlightLevel, 0.95, 1);
        Assert.InRange(rig.Glyph.GlyphScale, TransportGlyph.PressScale - 0.01, TransportGlyph.PressScale + 0.01);

        rig.Up();
        var peak = 0.0;
        for (var i = 0; i < 40; i++)
        {
            rig.Frame();
            peak = Math.Max(peak, rig.Glyph.GlyphScale);
        }
        Assert.InRange(peak, 1.02, 1.07);   // Apple's release overshoot (measured ≈1.05)
        rig.Settle();
        Assert.Equal(1, rig.Glyph.GlyphScale);
        Assert.Equal(0, rig.Glyph.HighlightLevel);
        Assert.Equal(1, rig.Command.Count);
        rig.Window.Close();
    }

    /// <summary>A frame only reads springs and invalidates the visual: no allocation, so a
    /// running press never feeds the GC (the phone's frame budget).</summary>
    [AvaloniaFact]
    public void Frames_AllocateNothing()
    {
        var rig = Open(TransportKind.Next);
        rig.Down();
        rig.Up();
        rig.Frame();   // the events are handled here; the frames after only advance
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 30; i++) rig.Frame();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"{allocated} bytes over 30 frames");
        rig.Settle();
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void QuickTap_StillShowsTheDip_BeforeSpringingBack()
    {
        var rig = Open(TransportKind.Previous);
        rig.Down();
        rig.Up();   // same frame: a very quick tap
        var min = 1.0;
        for (var i = 0; i < 30; i++)
        {
            rig.Frame();
            min = Math.Min(min, rig.Glyph.GlyphScale);
        }
        Assert.InRange(min, TransportGlyph.PressScale - 0.01, 0.9);
        AssertSmoothUntilSettled(rig);
        Assert.Equal(1, rig.Glyph.GlyphScale);
        rig.Window.Close();
    }

    /// <summary>
    /// Frames until nothing moves, failing on any value that jumps between two 60 fps frames
    /// more than its curve ever moves in one (a skipped-ahead spring, a reset, a pop). The
    /// bounds sit just above the fastest legitimate step: the circle fading in (ω 55, ~0.31 per
    /// frame) and the outgoing morph glyph (ω 48, ~0.25).
    /// </summary>
    private static void AssertSmoothUntilSettled(Rig rig)
    {
        var g = rig.Glyph;
        double[] Read() => new[] { g.GlyphScale, g.HighlightLevel, g.PlayScale, g.PlayOpacity, g.PauseScale, g.PauseOpacity };
        var bounds = new[] { 0.05, 0.35, 0.26, 0.26, 0.26, 0.26 };
        var names = new[] { "scale", "highlight", "playScale", "playOpacity", "pauseScale", "pauseOpacity" };
        var last = Read();
        for (var i = 0; i < 3 * Fps && g.IsMoving; i++)
        {
            rig.Frame();
            var now = Read();
            for (var k = 0; k < now.Length; k++)
                Assert.True(Math.Abs(now[k] - last[k]) <= bounds[k], $"{names[k]} jumped {last[k]:0.000} → {now[k]:0.000} at frame {i}");
            last = now;
        }
        Assert.False(g.IsMoving, "still moving after 3 s");
    }

    [AvaloniaFact]
    public void Click_RunsOneConveyorStep_WithSmoothMonotonicFrames()
    {
        var rig = Open(TransportKind.Next);
        rig.Down();
        rig.Up();
        var last = -1.0;
        var lastScale = 1.0;
        var frames = 0;
        rig.Frame();
        Assert.True(rig.Glyph.IsSliding);
        while (rig.Glyph.IsSliding)
        {
            var u = rig.Glyph.SlideProgress;
            Assert.True(u > last, $"slide went back at frame {frames}: {u} after {last}");
            Assert.True(u - Math.Max(0, last) < 0.2, $"slide jumped at frame {frames}: {last} → {u}");
            Assert.True(Math.Abs(rig.Glyph.GlyphScale - lastScale) < 0.05, $"scale jumped at frame {frames}");
            last = u;
            lastScale = rig.Glyph.GlyphScale;
            rig.Frame();
            frames++;
        }
        // 220 ms at 60 fps.
        Assert.InRange(frames, 12, 14);
        AssertSmoothUntilSettled(rig);
        Assert.Equal(0, rig.Glyph.SlideProgress);
        Assert.Equal(1, rig.Glyph.GlyphScale);
        Assert.Equal(1, rig.Command.Count);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void RapidClicks_ExecuteOncePerClick_AndEndExactlyAtRest()
    {
        var rig = Open(TransportKind.Next);
        for (var i = 0; i < 10; i++)
        {
            rig.Down();
            rig.Frame();
            rig.Up();
            rig.Frame();
        }
        Assert.Equal(10, rig.Command.Count);
        AssertSmoothUntilSettled(rig);
        Assert.False(rig.Glyph.IsSliding);
        Assert.Equal(0, rig.Glyph.SlideProgress);
        Assert.Equal(1, rig.Glyph.GlyphScale);
        Assert.Equal(0, rig.Glyph.HighlightLevel);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void EnterKey_ClicksOnce_WithTheDipAndTheSlide()
    {
        var rig = Open(TransportKind.Previous);
        rig.Button.Focus();
        rig.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        rig.Window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        rig.Frame();
        Assert.True(rig.Glyph.IsSliding);
        rig.Run(0.1);
        Assert.True(rig.Glyph.GlyphScale < 0.95);
        rig.Settle();
        Assert.Equal(1, rig.Command.Count);
        Assert.Equal(1, rig.Glyph.GlyphScale);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void PlayStateChange_MorphsAndEndsOnTheNewGlyph()
    {
        var rig = Open(TransportKind.PlayPause);
        Assert.Equal(1, rig.Glyph.PlayOpacity);
        Assert.Equal(0, rig.Glyph.PauseOpacity);

        rig.Glyph.IsPlaying = true;
        rig.Run(0.05);
        // Mid-morph: Play shrinking and fading, Pause growing in.
        Assert.InRange(rig.Glyph.PlayOpacity, 0.05, 0.95);
        Assert.InRange(rig.Glyph.PlayScale, TransportGlyph.MorphHiddenScale, 0.99);
        Assert.InRange(rig.Glyph.PauseScale, TransportGlyph.MorphEnterScale, 0.99);
        var peak = 0.0;
        for (var i = 0; i < 40; i++)
        {
            rig.Frame();
            peak = Math.Max(peak, rig.Glyph.PauseScale);
        }
        Assert.InRange(peak, 1.03, 1.12);   // the small overshoot Apple's incoming glyph has
        AssertSmoothUntilSettled(rig);
        Assert.Equal(1, rig.Glyph.PauseOpacity);
        Assert.Equal(1, rig.Glyph.PauseScale);
        Assert.Equal(0, rig.Glyph.PlayOpacity);

        // A double toggle mid-morph turns around and still lands on Play.
        rig.Glyph.IsPlaying = false;
        rig.Run(0.08);
        rig.Glyph.IsPlaying = true;
        rig.Run(0.03);
        rig.Glyph.IsPlaying = false;
        AssertSmoothUntilSettled(rig);
        Assert.Equal(1, rig.Glyph.PlayOpacity);
        Assert.Equal(1, rig.Glyph.PlayScale);
        Assert.Equal(0, rig.Glyph.PauseOpacity);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void PlayStateArrivingWithTheBind_IsShownWithoutAMorph()
    {
        var glyph = new TransportGlyph { Kind = TransportKind.PlayPause, Width = 20, Height = 20 };
        var window = new Window { Content = new Button { Content = glyph } };
        window.Show();
        glyph.IsPlaying = true;   // before the dispatcher has finished the mount
        Assert.False(glyph.IsMoving);
        Assert.Equal(1, glyph.PauseOpacity);
        Assert.Equal(0, glyph.PlayOpacity);
        window.Close();
    }

    [AvaloniaFact]
    public void ReducedMotion_MakesEverythingInstant()
    {
        var rig = Open(TransportKind.Next);
        TransportGlyph.SetReduceMotion(rig.Window, true);
        rig.Down();
        Assert.Equal(1, rig.Glyph.HighlightLevel);   // the circle still answers the touch
        Assert.Equal(1, rig.Glyph.GlyphScale);
        Assert.False(rig.Glyph.IsMoving);
        rig.Up();
        Assert.Equal(0, rig.Glyph.HighlightLevel);
        Assert.False(rig.Glyph.IsSliding);
        Assert.False(rig.Glyph.IsMoving);
        Assert.Equal(1, rig.Command.Count);
        rig.Window.Close();

        var play = Open(TransportKind.PlayPause);
        TransportGlyph.SetReduceMotion(play.Window, true);
        play.Glyph.IsPlaying = true;
        Assert.False(play.Glyph.IsMoving);
        Assert.Equal(1, play.Glyph.PauseOpacity);
        Assert.Equal(0, play.Glyph.PlayOpacity);
        play.Window.Close();
    }

    /// <summary>The glyph draws the very paths of Icons.axaml (and the phone's Ph* twins):
    /// Play, Pause, FastForward, its trailing triangle, and RewindIcon as FastForward mirrored on
    /// the 24 grid.</summary>
    [Fact]
    public void TheGlyph_DrawsTheIconDictionarysPaths()
    {
        var desktop = Geometries(Path.Combine("src", "Noctis.UI", "Assets", "Icons.axaml"));
        var phone = Geometries(Path.Combine("src", "Noctis.Mobile", "Views", "PhoneIcons.axaml"));
        Assert.Equal(desktop["PlayIcon"], TransportGlyph.PlayData);
        Assert.Equal(desktop["PauseIcon"], TransportGlyph.PauseData);
        Assert.Equal(desktop["FastForwardIcon"], TransportGlyph.ForwardData);
        Assert.Equal(phone["PhForwardFill"], TransportGlyph.ForwardData + "M0 0ZM24 24Z");
        Assert.StartsWith(TransportGlyph.TriangleData, TransportGlyph.ForwardData);

        // RewindIcon = every x mirrored (24 - x).
        var figures = TransportGlyph.ForwardData.Split('M', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => "M" + System.Text.RegularExpressions.Regex.Replace(f, @"([\d.]+) ([\d.]+)",
                m => $"{Math.Round(24 - double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 3).ToString(System.Globalization.CultureInfo.InvariantCulture)} {m.Groups[2].Value}"));
        Assert.Equal(desktop["RewindIcon"], string.Concat(figures));
    }

    private static Dictionary<string, string> Geometries(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Noctis.sln"))) dir = dir.Parent;
        var text = File.ReadAllText(Path.Combine(dir!.FullName, relativePath));
        return System.Text.RegularExpressions.Regex.Matches(text, "<StreamGeometry x:Key=\"(\\w+)\"\\s*>(.*?)</StreamGeometry>", System.Text.RegularExpressions.RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
    }

    [AvaloniaFact]
    public void TheGlyph_LeavesTheButtonsSizeAndHitAreaAlone()
    {
        var rig = Open(TransportKind.Next);
        Assert.Equal(new Size(34, 34), rig.Button.Bounds.Size);
        Assert.Equal(new Size(18, 18), rig.Glyph.Bounds.Size);
        Assert.False(rig.Glyph.IsHitTestVisible);
        // A press anywhere on the button (its corner, outside the glyph) still clicks it.
        var corner = rig.Button.TranslatePoint(new Point(3, 3), rig.Window)!.Value;
        rig.Window.MouseDown(corner, MouseButton.Left);
        rig.Window.MouseUp(corner, MouseButton.Left);
        Assert.Equal(1, rig.Command.Count);
        Assert.Equal("Next", Avalonia.Automation.AutomationProperties.GetName(rig.Button));
        rig.Window.Close();
    }
}
