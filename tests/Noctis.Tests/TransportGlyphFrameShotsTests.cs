using System.IO;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Frame-by-frame renders of the transport press, laid out like the cropped Apple Music
/// recording (a 920×220 row, buttons centred at x 137 / 455 / 776, skip box 144, Play box 160,
/// so the press circle comes out at Apple's 178 px), on a fixed 60 fps clock. Probe only:
/// runs with NOCTIS_TEST_SKIA=1 and NOCTIS_TRANSPORT_SHOTS=&lt;dir&gt;, and writes one PNG per
/// frame plus frames.csv (time and every animated value) for the contact sheet.
/// </summary>
public sealed class TransportGlyphFrameShotsTests : IDisposable
{
    public TransportGlyphFrameShotsTests() => TransportGlyph.ManualFrames = true;
    public void Dispose() => TransportGlyph.ManualFrames = false;

    private sealed class Toggle(Func<TransportGlyph> glyph) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => glyph().IsPlaying = !glyph().IsPlaying;
    }

    private sealed class Nop : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }

    [AvaloniaFact]
    public void RenderPressSequences()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_TRANSPORT_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);

        TransportGlyph? playGlyph = null;
        Button Make(TransportKind kind, double box, double x, ICommand command, out TransportGlyph glyph)
        {
            glyph = new TransportGlyph { Kind = kind, Width = box, Height = box, Foreground = Brushes.White, IsPlaying = kind == TransportKind.PlayPause };
            var b = new Button
            {
                Width = 200, Height = 200, Padding = default, Background = Brushes.Transparent,
                BorderThickness = default, Command = command, Content = glyph,
            };
            Canvas.SetLeft(b, x - 100);
            Canvas.SetTop(b, 12);
            return b;
        }
        var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x22, 0x20)) };
        canvas.Children.Add(Make(TransportKind.Previous, 144, 137, new Nop(), out var prev));
        canvas.Children.Add(Make(TransportKind.PlayPause, 160, 455, new Toggle(() => playGlyph!), out playGlyph));
        canvas.Children.Add(Make(TransportKind.Next, 144, 776, new Nop(), out var next));
        // Fluent's own pressed/hover fills would sit under the circle; the probe wants the glyph alone.
        foreach (var key in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed",
                     "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed" })
            canvas.Resources[key] = Brushes.Transparent;
        var window = new Window { Width = 920, Height = 220, Content = canvas };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        using var csv = new StreamWriter(Path.Combine(dir, "frames.csv"));
        csv.WriteLine("seq,frame,t,scale,highlight,slide,playScale,playOpacity,pauseScale,pauseOpacity,renderMs");
        var now = 100.0;

        void Sequence(string name, Button button, TransportGlyph glyph, int frames, int downAt, int upAt)
        {
            var centre = button.TranslatePoint(new Point(100, 100), window)!.Value;
            for (var i = 0; i < frames; i++)
            {
                if (i == downAt) window.MouseDown(centre, MouseButton.Left);
                if (i == upAt) window.MouseUp(centre, MouseButton.Left);
                now += 1.0 / 60;
                prev.OnFrame(now);
                playGlyph!.OnFrame(now);
                next.OnFrame(now);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var frame = window.CaptureRenderedFrame()!;
                sw.Stop();
                frame.Save(Path.Combine(dir, $"{name}_{i:000}.png"));
                csv.WriteLine(FormattableString.Invariant(
                    $"{name},{i},{i / 60.0:0.0000},{glyph.GlyphScale:0.0000},{glyph.HighlightLevel:0.0000},{glyph.SlideProgress:0.0000},{glyph.PlayScale:0.0000},{glyph.PlayOpacity:0.0000},{glyph.PauseScale:0.0000},{glyph.PauseOpacity:0.0000},{sw.Elapsed.TotalMilliseconds:0.00}"));
            }
        }

        // Timed like the recording: Pause pressed at its frame 12, lifted at 18 (morph to Play),
        // then Play pressed at 54 and lifted at 59; Previous pressed at 218, lifted at 222; Next
        // at 351 / 356. Each sequence starts 4 frames before its press.
        Sequence("pause_to_play", playGlyph.Parent as Button ?? throw new InvalidOperationException(), playGlyph, 50, 4, 10);
        Sequence("play_to_pause", (Button)playGlyph.Parent!, playGlyph, 50, 1, 5);
        Sequence("prev", (Button)prev.Parent!, prev, 50, 4, 7);
        Sequence("next", (Button)next.Parent!, next, 50, 4, 9);
        window.Close();
    }
}
