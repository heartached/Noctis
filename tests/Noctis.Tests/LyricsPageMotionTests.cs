using Noctis.Models;
using Noctis.Services;
using Noctis.Services.LyricsPage;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

public class LyricsPageMotionTests
{
    private static readonly LyricsPageGeometry G = LyricsPageLayout.Measure(ShareCardFormat.Story, true);

    private static (LyricsPageMotion Motion, List<LyricLine> Lines, IReadOnlyList<PageLineLayout> Layout) Build(
        IReadOnlyList<LyricsPageLine>? source = null)
    {
        var lines = LyricsPageEngine.BuildLines(source ?? LyricsPageTestData.Song());
        using var face = ShareCardRenderer.ResolveTypeface(SKFontStyleWeight.Bold);
        var layout = LyricsPageLineLayout.Build(lines, G, face);
        var motion = new LyricsPageMotion(lines, layout.Select(l => l.Top).ToList(),
            layout.Select(l => l.Height).ToList(), G.AnchorY, G.Viewport.Height, G.Scale);
        return (motion, lines, layout);
    }

    private static int IndexAt(List<LyricLine> lines, double seconds) =>
        lines.FindIndex(l => !l.IsIntroPlaceholder && l.Timestamp is { } ts && Math.Abs(ts.TotalSeconds - seconds) < 1e-6);

    private static double Frame(int n, ShareClipTiming timing) => LyricsPageFrames.TrackTime(n, LyricsPageFrames.Fps, timing);

    [Fact]
    public void The_list_glides_to_each_line_on_the_page_spring_and_lands_exactly()
    {
        var (motion, lines, layout) = Build();
        int first = IndexAt(lines, 10), second = IndexAt(lines, 14);
        double pitch = layout[second].Top - layout[first].Top;

        // The first frame snaps to the line being sung.
        motion.Advance(10.5);
        Assert.Equal(first, motion.ScrollIndex);
        Assert.Equal(motion.TargetOffset(first), motion.ScrollOffset(10.5));

        var timing = new ShareClipTiming(10, 10);
        double prev = motion.ScrollOffset(10.5);
        double? glideStart = null;
        for (int n = 0; n < LyricsPageFrames.Count(timing, LyricsPageFrames.Fps); n++)
        {
            double t = Frame(n, timing);
            if (t <= 10.5 || t > 16) continue;
            motion.Advance(t);
            double offset = motion.ScrollOffset(t);

            // Forward only (the spring's sub-pixel overshoot settling back aside), in small steps.
            Assert.True(offset >= prev - 0.01 * pitch, $"offset went back at {t:F3}: {prev} → {offset}");
            Assert.True(offset - prev < 0.35 * pitch, $"step too large at {t:F3}: {offset - prev}");
            if (glideStart == null && offset != prev) glideStart = t;
            prev = offset;

            if (t < 14 - LineMotionLeadSeconds)
                Assert.Equal(motion.TargetOffset(first), offset);
            if (t >= 15.0)
            {
                Assert.Equal(second, motion.ScrollIndex);
                Assert.Equal(motion.TargetOffset(second), offset);
                Assert.All(motion.LineLags(t), lag => Assert.Equal(0.0, lag));
            }
        }
        Assert.NotNull(glideStart);
        Assert.True(glideStart >= 14 - LineMotionLeadSeconds);
    }

    private const double LineMotionLeadSeconds = Noctis.Helpers.LineMotion.ScrollLeadMs / 1000.0;

    [Fact]
    public void Lines_below_the_target_cascade_behind_the_list_while_it_glides()
    {
        var (motion, lines, _) = Build();
        int second = IndexAt(lines, 14);
        motion.Advance(13.0);
        motion.Advance(13.3);
        Assert.Equal(second, motion.ScrollIndex);

        motion.Advance(13.45);
        var lags = motion.LineLags(13.45);
        Assert.Equal(lines.Count, lags.Count);
        for (int i = 0; i <= second; i++) Assert.Equal(0.0, lags[i], 9);
        // Each line below trails the one above it, by at most the clamp's ceiling.
        for (int i = second + 1; i < lags.Count; i++)
        {
            Assert.True(lags[i] >= lags[i - 1] - 1e-9, $"line {i} ahead of line {i - 1}");
            Assert.True(lags[i] - lags[i - 1] <= 2 * 16 * G.Scale + 1e-9);
        }
        Assert.True(lags[second + 1] > 0);
        // The same array every frame: no per-frame allocation.
        Assert.Same(lags, motion.LineLags(13.5));
    }

    [Fact]
    public void Same_frame_times_give_identical_offsets()
    {
        var timing = new ShareClipTiming(10, 10);
        List<double> Run()
        {
            var (motion, _, _) = Build();
            var seq = new List<double>();
            for (int n = 0; n <= 600; n++)
            {
                double t = Frame(n, timing);
                motion.Advance(t);
                seq.Add(motion.ScrollOffset(t));
                seq.AddRange(motion.LineLags(t));
            }
            return seq;
        }
        var a = Run();
        var b = Run();
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
            Assert.Equal(BitConverter.DoubleToInt64Bits(a[i]), BitConverter.DoubleToInt64Bits(b[i]));
    }

    [Fact]
    public void A_seek_back_snaps_instead_of_gliding()
    {
        var (motion, lines, _) = Build();
        motion.Advance(20);
        motion.Advance(11);
        int first = IndexAt(lines, 10);
        Assert.Equal(first, motion.ScrollIndex);
        Assert.Equal(motion.TargetOffset(first), motion.ScrollOffset(11));
        Assert.All(motion.LineLags(11), lag => Assert.Equal(0.0, lag));
    }

    [Fact]
    public void Range_at_the_song_edges_scrolls_and_focuses_without_neighbours()   // Review Focus 1
    {
        var sources = new[]
        {
            (IReadOnlyList<LyricsPageLine>)new[] { LyricsPageTestData.Words(10, "only line here") },
            LyricsPageTestData.Song(),
            new[] { LyricsPageTestData.Words(0, "starts at zero"), LyricsPageTestData.Words(3, "and then this") },
            Array.Empty<LyricsPageLine>(),
        };
        foreach (var source in sources)
        {
            var (motion, _, _) = Build(source);
            var timing = new ShareClipTiming(0, 16);
            for (int n = 0; n < LyricsPageFrames.Count(timing, LyricsPageFrames.Fps); n++)
            {
                double t = Frame(n, timing);
                motion.Advance(t);
                Assert.True(double.IsFinite(motion.ScrollOffset(t)));
                Assert.All(motion.LineLags(t), lag => Assert.True(double.IsFinite(lag)));
            }
        }

        // The last line of the song, held to the end of the clip.
        var (end, lines, _) = Build();
        int last = lines.Count - 1;
        var tail = new ShareClipTiming(50, 20);
        for (int n = 0; n < LyricsPageFrames.Count(tail, LyricsPageFrames.Fps); n++)
        {
            double t = Frame(n, tail);
            end.Advance(t);
            if (t < 57.5) continue;
            Assert.Equal(last, end.ScrollIndex);
            Assert.Equal(end.TargetOffset(last), end.ScrollOffset(t));
        }
    }
}
