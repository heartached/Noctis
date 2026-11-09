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

    /// <summary>
    /// Bug: the glide landed on an unrounded offset and the renderer then rounded the resting
    /// line onto a whole pixel, a visible up-to-0.5 px tick right after every glide. The target
    /// offsets are rounded instead, so the glide itself ends with the line on a device pixel.
    /// </summary>
    [Theory]
    [InlineData(0.5f)]
    [InlineData(1f)]
    [InlineData(2f)]
    public void The_glide_lands_its_line_on_a_whole_device_pixel(float pixelScale)
    {
        var lines = LyricsPageEngine.BuildLines(LyricsPageTestData.Song());
        using var face = ShareCardRenderer.ResolveTypeface(SKFontStyleWeight.Bold);
        var layout = LyricsPageLineLayout.Build(lines, G, face);
        var motion = new LyricsPageMotion(lines, layout.Select(l => l.Top).ToList(),
            layout.Select(l => l.Height).ToList(), G.AnchorY, G.Viewport.Height, G.Scale, G.Viewport.Top, pixelScale);

        foreach (var index in Enumerable.Range(0, lines.Count))
        {
            double y = (G.Viewport.Top + layout[index].Top - motion.TargetOffset(index)) * pixelScale;
            Assert.Equal(Math.Round(y), y, 3);
            // Within half a device pixel of the unrounded centre-on-anchor offset.
            double exact = layout[index].Top + layout[index].Height / 2.0 - G.AnchorY;
            Assert.InRange(motion.TargetOffset(index), exact - 0.5 / pixelScale - 1e-6, exact + 0.5 / pixelScale + 1e-6);
        }

        int second = IndexAt(lines, 14);
        var timing = new ShareClipTiming(10, 10);
        for (int n = 0; n < LyricsPageFrames.Count(timing, LyricsPageFrames.Fps); n++)
        {
            double t = Frame(n, timing);
            if (t > 15.5) break;
            motion.Advance(t);
        }
        Assert.Equal(second, motion.ScrollIndex);
        Assert.Equal(motion.TargetOffset(second), motion.ScrollOffset(15.5));
    }

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

    [Fact]
    public void Neighbours_dim_and_blur_by_distance_on_a_half_second_ease()
    {
        var (motion, lines, _) = Build();
        int active = IndexAt(lines, 10), next = IndexAt(lines, 14);
        var timing = new ShareClipTiming(10, 10);
        for (int n = 0; Frame(n, timing) < 12.0; n++) motion.Advance(Frame(n, timing));

        // Mid-line, settled.
        motion.Advance(12.0);
        Assert.Equal(1.0, motion.Opacity(active, 12.0));
        Assert.Equal(0.55, motion.Opacity(active - 1, 12.0));
        Assert.Equal(0.55, motion.Opacity(active + 1, 12.0));
        Assert.Equal(0.32, motion.Opacity(active + 2, 12.0));
        Assert.Equal(0.0, motion.Opacity(active + 10, 12.0));
        Assert.Equal(0.0, motion.BlurRadius(active, 12.0));
        Assert.Equal(4.0 * G.Scale, motion.BlurRadius(active - 1, 12.0), 9);
        Assert.Equal(4.0 * G.Scale, motion.BlurRadius(active + 1, 12.0), 9);
        Assert.Equal(10.0 * G.Scale, motion.BlurRadius(active + 4, 12.0), 9);
        Assert.Equal(10.0 * G.Scale, motion.BlurRadius(active + 5, 12.0), 9);

        // The list leads into the next line: the old line eases out over 0.5 s, blur with it.
        for (int n = 0; Frame(n, timing) < 13.3; n++)
            if (Frame(n, timing) > 12.0) motion.Advance(Frame(n, timing));
        motion.Advance(13.3);
        Assert.Equal(next, motion.ScrollIndex);
        motion.Advance(13.55);
        double mid = motion.Opacity(active, 13.55);
        Assert.True(mid > 0.55 && mid < 1.0, $"opacity {mid}");
        double blurMid = motion.BlurRadius(active, 13.55);
        Assert.True(blurMid > 0 && blurMid < 4.0 * G.Scale, $"blur {blurMid}");
        motion.Advance(13.8);
        Assert.Equal(0.55, motion.Opacity(active, 13.8));
        Assert.Equal(1.0, motion.Opacity(next, 13.8));
        Assert.Equal(4.0 * G.Scale, motion.BlurRadius(active, 13.8), 9);
        Assert.Equal(0.0, motion.BlurRadius(next, 13.8));
    }

    [Fact]
    public void The_lead_line_takes_the_karaoke_look_and_the_sung_line_leaves_it()
    {
        var (motion, lines, _) = Build();
        int first = IndexAt(lines, 10), second = IndexAt(lines, 14);
        motion.Advance(13.0);
        Assert.Equal(1.0, motion.SweepFocus(first, 13.0));
        Assert.Equal(0.0, motion.SweepFocus(second, 13.0));
        motion.Advance(13.3);                                   // the list leads into the next line
        motion.Advance(13.8);
        Assert.Equal(1.0, motion.SweepFocus(second, 13.8));
        Assert.Equal(1.0, motion.SweepFocus(first, 13.8));      // still active until 14 s
        motion.Advance(14.0);                                   // the next line activates
        motion.Advance(14.25);
        double mid = motion.SweepFocus(first, 14.25);
        Assert.True(mid > 0 && mid < 1, $"sweep focus {mid}");
        motion.Advance(14.5);
        Assert.Equal(0.0, motion.SweepFocus(first, 14.5));
    }

    [Fact]
    public void Interlude_dots_fill_and_breathe_through_the_gap()
    {
        var (motion, lines, _) = Build();
        int dots = lines.FindIndex(l => l.IsInterlude);
        const int intro = 0;
        double dotsStart = lines[dots].Timestamp!.Value.TotalSeconds;
        double next = lines[dots + 1].Timestamp!.Value.TotalSeconds;

        // Walk into the gap frame by frame; the breathing starts when the dots become the active line.
        var timing = new ShareClipTiming(38, 20);
        double activation = double.NaN;
        for (int n = 0; double.IsNaN(activation) && Frame(n, timing) < 45; n++)
        {
            double t = Frame(n, timing);
            motion.Advance(t);
            Assert.Equal(1.0, motion.DotsScale(intro, t));           // inactive dots never breathe
            Assert.Equal(1.0, motion.DotsScale(dots, t));            // not breathing yet, or at phase 0
            if (motion.Timeline.ActiveIndex == dots) activation = t;
        }
        Assert.False(double.IsNaN(activation));
        motion.Advance(activation + 2.1);
        Assert.Equal(0.88, motion.DotsScale(dots, activation + 2.1), 9);
        motion.Advance(activation + 3.0);
        Assert.InRange(motion.DotsScale(dots, activation + 3.0), 0.88, 1.0);
        motion.Advance(activation + 4.2);
        Assert.Equal(1.0, motion.DotsScale(dots, activation + 4.2), 9);

        // Mid-gap: the dots are the focus and fill with the gap.
        double midGap = (dotsStart + next) / 2;
        motion.Advance(midGap);
        Assert.True(motion.Timeline.ActiveLine!.IsInterlude);
        Assert.Equal(1.0, motion.Opacity(dots, midGap));
        Assert.Equal((midGap - dotsStart) / (next - dotsStart), lines[dots].InterludeProgress, 6);
        Assert.InRange(motion.DotsScale(dots, midGap), 0.88, 1.0);

        // A seek into the gap breathes from the dots' own activation time, not from the seek.
        var (seeked, _, _) = Build();
        double t0 = dotsStart - Noctis.Helpers.LineMotion.HalfTravelMs / 1000.0;
        seeked.Advance(t0 + 2.1);
        Assert.Equal(dots, seeked.Timeline.ActiveIndex);
        Assert.Equal(0.88, seeked.DotsScale(dots, t0 + 2.1), 9);
    }

    [Fact]
    public void Overlapping_duet_lines_are_both_in_focus_and_the_list_holds_on_the_first()   // Review Focus 3
    {
        static KaraokeWord[] Words(double start, double end, params string[] tokens)
        {
            double step = (end - start) / tokens.Length;
            return tokens.Select((t, i) => new KaraokeWord(t, start + i * step, start + (i + 1) * step)).ToArray();
        }
        var (motion, lines, _) = Build(new[]
        {
            new LyricsPageLine { Text = "one two three four five", StartSeconds = 10,
                Words = Words(10, 15, "one", "two", "three", "four", "five") },
            new LyricsPageLine { Text = "six seven", StartSeconds = 12, Voice = LyricVoice.Voice2,
                Words = Words(12, 14, "six", "seven") },
        });
        int a = IndexAt(lines, 10), b = IndexAt(lines, 12);
        var timing = new ShareClipTiming(10, 4);
        for (int n = 0; Frame(n, timing) < 13; n++) motion.Advance(Frame(n, timing));
        motion.Advance(13);
        Assert.Equal(b, motion.Timeline.ActiveIndex);
        Assert.Equal(a, motion.ScrollIndex);
        Assert.Equal(1.0, motion.Opacity(a, 13));
        Assert.Equal(1.0, motion.Opacity(b, 13));
        Assert.Equal(0.0, motion.BlurRadius(a, 13));
        Assert.Equal(0.0, motion.BlurRadius(b, 13));
        Assert.Equal(motion.TargetOffset(a), motion.ScrollOffset(13));
    }

    [Fact]
    public void An_adlib_only_line_without_an_end_still_bounds_the_interlude()   // Unit C carry
    {
        var (motion, lines, _) = Build(new[]
        {
            LyricsPageTestData.Words(10, "hold me closer"),
            new LyricsPageLine { Text = "ooh yeah", StartSeconds = 14, IsBackgroundOnly = true,
                BackgroundWords = new[] { new KaraokeWord("ooh", 14, 15), new KaraokeWord("yeah", 15, 16) } },
            LyricsPageTestData.Words(30, "tiny dancer"),
        });
        // LyricsTimeline.SungEnd reads BackgroundEndTimestamp: the gap counts from the adlib's end.
        int dots = lines.FindIndex(l => l.IsInterlude);
        Assert.True(dots > 0, "no interlude after the adlib-only line");
        Assert.Equal(16 + 0.35, lines[dots].Timestamp!.Value.TotalSeconds, 3);
        motion.Advance(20);
        Assert.Equal(dots, motion.Timeline.ActiveIndex);
        Assert.Equal(dots, motion.ScrollIndex);
        Assert.Equal(1.0, motion.Opacity(dots, 20));
    }

    [Fact]
    public void The_focus_ease_is_the_pages_cubic_in_out()
    {
        Assert.Equal(0.0, KaraokeFlow.EaseCubicInOut(0));
        Assert.Equal(0.5, KaraokeFlow.EaseCubicInOut(0.5));
        Assert.Equal(1.0, KaraokeFlow.EaseCubicInOut(1));
        Assert.Equal(4 * 0.25 * 0.25 * 0.25, KaraokeFlow.EaseCubicInOut(0.25), 12);
        Assert.Equal(0.0, KaraokeFlow.EaseCubicInOut(-1));
        Assert.Equal(1.0, KaraokeFlow.EaseCubicInOut(2));
    }
}
