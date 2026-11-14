using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #233: opt-in frame-cost probe for the album carousel's glide (reflections on every
/// style, the glow). Runs only with NOCTIS_CAROUSEL_PROBE set to an output file (and
/// NOCTIS_TEST_SKIA=1 for real Skia rendering); appends bytes allocated and time per frame,
/// per style, over 600 glide frames. Uses the covers Styles_SaveRenders writes.
/// </summary>
public class AlbumCarouselPerfProbe
{
    [AvaloniaFact]
    public void Probe()
    {
        var outFile = Environment.GetEnvironmentVariable("NOCTIS_CAROUSEL_PROBE");
        if (string.IsNullOrEmpty(outFile)) return;
        var lines = new List<string>();
        foreach (var style in Enum.GetValues<AlbumCarouselStyle>())
        {
            var albums = Enumerable.Range(0, 60).Select(i => new Album { Id = Guid.NewGuid(), Name = $"A{i}", Artist = "X", ArtworkPath = Path.Combine(Path.GetTempPath(), "NoctisCarouselArt", $"art{i % 15}.png"), Tracks = new List<Track>() }).ToList();
            var c = new AlbumCarousel { Albums = albums, CaseStyle = style };
            var w = new Window { Width = 1280, Height = 560, Content = c, Background = new SolidColorBrush(Colors.Black) };
            w.Show();
            c.SnapTo(10);
            var warm = Stopwatch.StartNew();
            while (warm.ElapsedMilliseconds < 800) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(10); }
            long bytes = 0;
            var sw = new Stopwatch();
            var frames = 0;
            for (var step = 0; step < 20; step++)
            {
                c.MoveTo(step % 2 == 0 ? 30 : 10);
                for (var f = 0; f < 30; f++)
                {
                    var a0 = GC.GetAllocatedBytesForCurrentThread();
                    sw.Start();
                    c.StepGlide(1 / 60.0);
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    sw.Stop();
                    bytes += GC.GetAllocatedBytesForCurrentThread() - a0;
                    frames++;
                }
            }
            lines.Add($"{style}: {bytes / frames} B/frame, {sw.Elapsed.TotalMilliseconds / frames:F3} ms/frame (UI thread incl. render tick)");
            w.Close();
        }
        File.AppendAllLines(outFile, lines);
    }
}
