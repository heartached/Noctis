using System.Runtime.InteropServices;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.LyricsPage;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

public class LyricsPageRenderTests
{
    private const float PreviewScale = 0.5f;

    private static LyricsPageSpec Spec(ShareCardFormat format, bool safeZones = true,
        IReadOnlyList<LyricsPageLine>? lines = null) => LyricsPageTestData.Spec(format, safeZones, lines);

    /// <summary>The frame at <paramref name="t"/> as BGRA premul bytes.</summary>
    private static byte[] Frame(LyricsPageAnimator animator, double t)
    {
        var buffer = new byte[animator.PixelWidth * animator.PixelHeight * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { animator.RenderFrame(t, handle.AddrOfPinnedObject(), animator.PixelWidth * 4); }
        finally { handle.Free(); }
        return buffer;
    }

    /// <summary>Only what sits behind the page: the background <see cref="PageBackgrounds"/> makes
    /// for <paramref name="spec"/>, no header and no lyrics.</summary>
    private static byte[] BackgroundOnly(LyricsPageSpec spec, int w, int h)
    {
        using var background = PageBackgrounds.Create(spec, w, h, LyricsPageFrames.Fps, true, null);
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Black);
        background.Draw(surface.Canvas, 0);
        var buffer = new byte[w * h * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { surface.ReadPixels(info, handle.AddrOfPinnedObject(), w * 4, 0, 0); }
        finally { handle.Free(); }
        return buffer;
    }

    /// <summary>Pixels wholly inside <paramref name="zone"/> (canvas px) at <paramref name="scale"/>.</summary>
    private static IEnumerable<(int X, int Y)> PixelsIn(SKRect zone, float scale, int w, int h)
    {
        int x0 = Math.Max(0, (int)Math.Ceiling(zone.Left * scale)), x1 = Math.Min(w, (int)Math.Floor(zone.Right * scale));
        int y0 = Math.Max(0, (int)Math.Ceiling(zone.Top * scale)), y1 = Math.Min(h, (int)Math.Floor(zone.Bottom * scale));
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                yield return (x, y);
    }

    private static int Brightness(byte[] px, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
    }

    /// <summary>The engine lines and their layout, built as the animator builds them.</summary>
    private static (List<LyricLine> Lines, IReadOnlyList<PageLineLayout> Layout) Layout(LyricsPageSpec spec, LyricsPageGeometry g)
    {
        var lines = LyricsPageEngine.BuildLines(spec.Lines);
        using var face = ShareCardRenderer.ResolveTypeface(SKFontStyleWeight.Bold);
        return (lines, LyricsPageLineLayout.Build(lines, g, face));
    }

    private static int IndexAt(List<LyricLine> lines, double seconds) =>
        lines.FindIndex(l => !l.IsIntroPlaceholder && l.Timestamp is { } ts && Math.Abs(ts.TotalSeconds - seconds) < 1e-6);

    /// <summary>Line <paramref name="index"/>'s top on the canvas at <paramref name="t"/> (after a draw at t).</summary>
    private static double LineTop(LyricsPageAnimator a, IReadOnlyList<PageLineLayout> layout, int index, double t) =>
        a.Geometry.Viewport.Top + layout[index].Top - a.Motion.ScrollOffset(t) + a.Motion.LineLags(t)[index];

    /// <summary>
    /// A side-zone pixel this much brighter (max channel) than the background is glyph ink: a
    /// full-opacity stroke lifts a pixel by 170-245 (a line moved into the right zone), the blur halo
    /// of the test song's lines by at most 10. Blur running into the side margins is allowed; text is not.
    /// </summary>
    private const int GlyphInkDelta = 128;

    /// <summary>
    /// What of <paramref name="frame"/> lands in the safe zones, against the background-only
    /// <paramref name="reference"/>: top/bottom zones (full canvas width) must equal the background
    /// exactly; side zones may carry halo but no pixel <see cref="GlyphInkDelta"/> brighter.
    /// Returns the violations and the largest side-zone lift seen.
    /// </summary>
    private static (List<string> Violations, int MaxSideLift) ZoneCheck(ShareCardFormat format, byte[] frame, byte[] reference, int w, int h)
    {
        var violations = new List<string>();
        int maxSideLift = 0;
        foreach (var zone in SocialSafeZones.Zones(format, true))
        {
            bool side = zone.Width < 1080;
            foreach (var (x, y) in PixelsIn(zone, PreviewScale, w, h))
            {
                int i = (y * w + x) * 4;
                int lift = Math.Max(frame[i] - reference[i], Math.Max(frame[i + 1] - reference[i + 1], frame[i + 2] - reference[i + 2]));
                bool same = frame[i] == reference[i] && frame[i + 1] == reference[i + 1] && frame[i + 2] == reference[i + 2];
                if (side)
                {
                    maxSideLift = Math.Max(maxSideLift, lift);
                    if (lift >= GlyphInkDelta) violations.Add($"side ({x},{y}) +{lift}");
                }
                else if (!same)
                {
                    violations.Add($"edge ({x},{y}) {frame[i]}/{reference[i]}");
                }
            }
        }
        return (violations, maxSideLift);
    }

    [Theory]
    [InlineData(ShareCardFormat.Story)]
    [InlineData(ShareCardFormat.Square)]
    public void No_lyric_or_header_pixel_lands_in_a_safe_zone(ShareCardFormat format)
    {
        var spec = Spec(format) with { Title = "A very long song title that would run across the whole header row", IsExplicit = true };
        using var animator = new LyricsPageAnimator(spec, PreviewScale);
        int w = animator.PixelWidth, h = animator.PixelHeight;
        var reference = BackgroundOnly(spec, w, h);
        Assert.NotEmpty(SocialSafeZones.Zones(format, true));

        foreach (var t in new[] { 10.1, 12.0, 13.4, 14.2, 25.0, 31.0 })
        {
            var (violations, maxSideLift) = ZoneCheck(format, Frame(animator, t), reference, w, h);
            Assert.True(violations.Count == 0,
                $"{format} t={t}: {violations.Count} zone pixels carry text (max side lift {maxSideLift}): {string.Join(" ", violations.Take(30))}");
        }

        // The check is not vacuous: outside the zones the frame does carry header and lyrics.
        var last = Frame(animator, 31.0);
        Assert.NotEqual(reference, last);
    }

    [Fact]
    public void Safe_zone_overlay_is_preview_only()
    {
        var spec = Spec(ShareCardFormat.Story);
        using var shown = new LyricsPageAnimator(spec, PreviewScale) { ShowSafeZones = true };
        using var hidden = new LyricsPageAnimator(spec, PreviewScale) { ShowSafeZones = false };
        int w = shown.PixelWidth, h = shown.PixelHeight;
        var reference = BackgroundOnly(spec, w, h);
        var withOverlay = Frame(shown, 12.0);
        var without = Frame(hidden, 12.0);

        // The overlay tints every zone pixel...
        int changed = 0, total = 0;
        foreach (var zone in SocialSafeZones.Zones(ShareCardFormat.Story, true))
            foreach (var (x, y) in PixelsIn(zone, PreviewScale, w, h))
            {
                int i = (y * w + x) * 4;
                total++;
                if (withOverlay[i] != without[i] || withOverlay[i + 1] != without[i + 1] || withOverlay[i + 2] != without[i + 2])
                    changed++;
            }
        Assert.Equal(total, changed);
        // ...and without it the zones hold only the background (and side-margin halo).
        Assert.Empty(ZoneCheck(ShareCardFormat.Story, without, reference, w, h).Violations);
        // Outside the zones the two frames are the same picture.
        var g = shown.Geometry;
        int mid = (int)((g.Viewport.Top + g.AnchorY) * PreviewScale) * w * 4 + (int)(540 * PreviewScale) * 4;
        Assert.Equal(without[mid], withOverlay[mid]);
    }

    [Fact]
    public void Identical_frame_times_render_identical_frames()
    {
        var spec = Spec(ShareCardFormat.Square);
        var timing = new ShareClipTiming(10, 8);
        using var a = new LyricsPageAnimator(spec, PreviewScale);
        using var b = new LyricsPageAnimator(spec, PreviewScale);
        byte[]? lastA = null, lastB = null;
        for (int n = 0; n <= 120; n++)
        {
            double t = LyricsPageFrames.TrackTime(n, LyricsPageFrames.Fps, timing);
            a.Draw(t);
            b.Draw(t);
            if (n == 120)
            {
                lastA = Frame(a, t);
                lastB = Frame(b, t);
            }
        }
        Assert.Equal(lastA, lastB);
    }

    [Fact]
    public void Settled_neighbour_lines_do_not_move_while_the_current_line_is_sung()
    {
        var spec = Spec(ShareCardFormat.Story);
        using var animator = new LyricsPageAnimator(spec, PreviewScale);
        var (lines, layout) = Layout(spec, animator.Geometry);
        int w = animator.PixelWidth;

        // Played through at 60 fps from 11.5 s. Frames 30/42 = 12.0/12.2 s: the first line is sung,
        // the intro dots sit above it. Frames 270/282 = 16.0/16.2 s: the second line is sung, the
        // glide from the first (from 13.3 s) has landed, the first line sits above.
        var captured = new Dictionary<int, (byte[] Frame, double Top)>();
        var pairs = new[] { (30, 42, 10.0), (270, 282, 14.0) };
        for (int n = 0; n <= 282; n++)
        {
            double t = 11.5 + n / 60.0;
            var pair = pairs.FirstOrDefault(p => p.Item1 == n || p.Item2 == n);
            if (pair == default)
            {
                animator.Draw(t);
                continue;
            }
            int above = IndexAt(lines, pair.Item3) - 1;
            captured[n] = (Frame(animator, t), LineTop(animator, layout, above, t));
        }

        foreach (var (from, to, active) in pairs)
        {
            int above = IndexAt(lines, active) - 1;
            Assert.True(above >= 0);
            var (first, top1) = captured[from];
            var (second, top2) = captured[to];
            Assert.Equal(top1, top2);

            int y0 = Math.Max(0, (int)Math.Floor(top1 * PreviewScale));
            int y1 = (int)Math.Ceiling((top1 + layout[above].Height) * PreviewScale);
            Assert.True(y1 > y0);
            var rows1 = first.AsSpan(y0 * w * 4, (y1 - y0) * w * 4).ToArray();
            var rows2 = second.AsSpan(y0 * w * 4, (y1 - y0) * w * 4).ToArray();
            Assert.Equal(rows1, rows2);
        }
    }

    [Fact]
    public void Interlude_dots_show_three_lit_dots_mid_gap()
    {
        var spec = Spec(ShareCardFormat.Story);
        using var animator = new LyricsPageAnimator(spec, PreviewScale);
        using var empty = new LyricsPageAnimator(spec with { Lines = Array.Empty<LyricsPageLine>() }, PreviewScale);
        var (lines, _) = Layout(spec, animator.Geometry);
        int dots = lines.FindIndex(l => l.IsInterlude);
        Assert.True(dots > 0);

        // Mid-gap (dots 39.85 → next line 51.5), at a breathing peak (two whole breaths after the
        // dots activated) so the dots sit at full size on their nominal centres.
        double activated = lines[dots].Timestamp!.Value.TotalSeconds - Noctis.Helpers.LineMotion.HalfTravelMs / 1000.0;
        double t = activated + 4 * 2.1;
        Assert.InRange(t, 44.0, 48.5);
        var frame = Frame(animator, t);
        var reference = Frame(empty, t);
        Assert.Equal(dots, animator.Motion.Timeline.ActiveIndex);
        Assert.InRange(animator.Motion.DotsScale(dots, t), 0.999, 1.0);

        var g = animator.Geometry;
        int w = animator.PixelWidth;
        float cy = g.Viewport.Top + g.AnchorY;
        for (int k = 0; k < 3; k++)
        {
            float cx = g.Viewport.Left + 16 * g.Scale + k * 20 * g.Scale;
            int x = (int)(cx * PreviewScale), y = (int)(cy * PreviewScale);
            Assert.True(Brightness(frame, w, x, y) > Brightness(reference, w, x, y) + 40,
                $"dot {k} at ({x},{y}) is not lit: {Brightness(frame, w, x, y)} vs {Brightness(reference, w, x, y)}");
        }
    }

    /// <summary>The brightest pixel in rows [y0, y1) between x0 and x1.</summary>
    private static int MaxInk(byte[] px, int w, int x0, int x1, int y0, int y1)
    {
        int max = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                max = Math.Max(max, Brightness(px, w, x, y));
        return max;
    }

    [Fact]
    public void Current_line_sweeps_and_line_synced_lines_light_whole()
    {
        // "hold me closer" at 10 s, 0.5 s a word: at 10.75 "hold" is sung, "closer" is not.
        var words = Spec(ShareCardFormat.Story);
        using (var animator = new LyricsPageAnimator(words, PreviewScale))
        {
            var (lines, layout) = Layout(words, animator.Geometry);
            int line = IndexAt(lines, 10);
            var frame = Frame(animator, 10.75);
            var (left, right) = Halves(animator, layout, line, 10.75, frame);
            Assert.True(left > right + 60, $"sung part {left} not brighter than unsung {right}");
        }

        var plain = Spec(ShareCardFormat.Story, lines: new[]
        {
            LyricsPageTestData.Plain(10, "hold me closer"),
            LyricsPageTestData.Plain(14, "tiny dancer now"),
        });
        using (var animator = new LyricsPageAnimator(plain, PreviewScale))
        {
            var (lines, layout) = Layout(plain, animator.Geometry);
            int line = IndexAt(lines, 10);
            var frame = Frame(animator, 10.75);
            var (left, right) = Halves(animator, layout, line, 10.75, frame);
            Assert.True(left > 200, $"line not lit: {left}");
            Assert.InRange(right, left - 20, left + 20);
        }
    }

    /// <summary>Brightest ink of the first token vs the last token of line <paramref name="line"/>'s first row.</summary>
    private static (int Left, int Right) Halves(LyricsPageAnimator a, IReadOnlyList<PageLineLayout> layout, int line, double t, byte[] frame)
    {
        var row = layout[line].Rows[0];
        double top = LineTop(a, layout, line, t);
        int y0 = (int)(top * PreviewScale), y1 = (int)((top + a.Geometry.RowHeight) * PreviewScale);
        int Px(float x) => (int)(x * PreviewScale);
        int left = MaxInk(frame, a.PixelWidth, Px(row.X + row.TokenX[0]), Px(row.X + row.TokenX[0] + row.TokenWidth[0]), y0, y1);
        int k = row.Tokens.Length - 1;
        int right = MaxInk(frame, a.PixelWidth, Px(row.X + row.TokenX[k] + row.TokenWidth[k] * 0.3f),
            Px(row.X + row.TokenX[k] + row.TokenWidth[k]), y0, y1);
        return (left, right);
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-page-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Export_writes_one_jpeg_per_frame_named_like_the_card_path()
    {
        var dir = TempDir();
        try
        {
            var progress = new List<(int Done, int Total)>();
            LyricsPageRenderer.RenderFrames(Spec(ShareCardFormat.Square), new ShareClipTiming(10, 0.5), 60, dir,
                (done, total) => progress.Add((done, total)));

            var files = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.Equal(Enumerable.Range(0, 30).Select(n => $"frame-{n:D5}.jpg"), files);
            foreach (var file in files)
            {
                using var bitmap = SKBitmap.Decode(Path.Combine(dir, file!));
                Assert.NotNull(bitmap);
                Assert.Equal(1080, bitmap.Width);
                Assert.Equal(1080, bitmap.Height);
            }
            Assert.Equal((30, 30), progress[^1]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Export_frames_carry_the_cover_and_its_colours()
    {
        var dir = TempDir();
        try
        {
            var cover = Path.Combine(dir, "cover.png");
            using (var bitmap = new SKBitmap(300, 300))
            {
                bitmap.Erase(new SKColor(0xE0, 0x20, 0x20));
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(cover, data.ToArray());
            }
            var frames = Path.Combine(dir, "frames");
            Directory.CreateDirectory(frames);
            var spec = Spec(ShareCardFormat.Square) with { ArtworkPath = cover };
            LyricsPageRenderer.RenderFrames(spec, new ShareClipTiming(10, 1.0 / 60), 60, frames);

            using var frame = SKBitmap.Decode(Path.Combine(frames, "frame-00000.jpg"));
            var g = LyricsPageLayout.Measure(ShareCardFormat.Square, true);
            // Header tile: the cover itself.
            var tile = frame.GetPixel((int)(g.Header.Left + g.ArtSize / 2), (int)(g.Header.Top + g.ArtSize / 2));
            Assert.True(tile.Red > 180 && tile.Green < 80 && tile.Blue < 80, $"tile {tile}");
            // Background (a corner in the side safe zone, no text): the cover's red under the scrim,
            // not the no-artwork #1A1A2E wash.
            var wash = frame.GetPixel(20, 1060);
            Assert.True(wash.Red > wash.Green + 40 && wash.Red > wash.Blue + 40, $"background {wash}");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Cancelling_stops_the_frame_loop()
    {
        var dir = TempDir();
        try
        {
            using var cts = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() =>
                LyricsPageRenderer.RenderFrames(Spec(ShareCardFormat.Square), new ShareClipTiming(10, 60), 60, dir,
                    (done, _) => { if (done >= 12) cts.Cancel(); }, cts.Token));
            Assert.True(Directory.GetFiles(dir).Length < 3600);
            Assert.True(Directory.GetFiles(dir).Length <= 13);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Still_is_mid_first_line()
    {
        Assert.Equal(10 + 2 + 0.08, LyricsPageRenderer.StillTime(new ShareClipTiming(10, 20), 14), 9);
        Assert.Equal(10 + 3 + 0.08, LyricsPageRenderer.StillTime(new ShareClipTiming(10, 6), null), 9);
    }

    [Fact]
    public void Still_png_is_the_frame_at_its_time()
    {
        var png = LyricsPageRenderer.RenderStillPng(Spec(ShareCardFormat.Story), 12.08, 0.5f);
        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal(540, bitmap.Width);
        Assert.Equal(960, bitmap.Height);
    }

    /// <summary>
    /// Bug: the still over a clip background asked ffmpeg for 60 frames a second, so a still
    /// 40 s into the clip piped ~2400 full-size frames for one picture. It asks for one a second.
    /// </summary>
    [Fact]
    public void Still_over_a_clip_asks_ffmpeg_for_one_frame_a_second()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lyrics-page-still-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var argsFile = Path.Combine(dir, "args.txt");
            string ffmpeg;
            if (OperatingSystem.IsWindows())
            {
                ffmpeg = Path.Combine(dir, "ffmpeg.cmd");
                File.WriteAllText(ffmpeg, $"@echo off\r\necho %* > \"{argsFile}\"\r\n");
            }
            else
            {
                ffmpeg = Path.Combine(dir, "ffmpeg");
                File.WriteAllText(ffmpeg, $"#!/bin/sh\necho \"$@\" > \"{argsFile}\"\n");
                File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var clip = Path.Combine(dir, "bg.mp4");
            File.WriteAllText(clip, "clip");
            var spec = Spec(ShareCardFormat.Story) with
            {
                BackgroundKind = LyricsPageBackgroundKind.Custom, BackgroundMediaPath = clip, FfmpegPath = ffmpeg,
            };

            var png = LyricsPageRenderer.RenderStillPng(spec, 40, 0.25f);   // the stand-in ends at once: the wash shows
            Assert.NotEmpty(png);
            Assert.True(File.Exists(argsFile), "ffmpeg was not started");
            var args = File.ReadAllText(argsFile);
            Assert.Contains(",fps=1,", args);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Still_render_stops_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            LyricsPageRenderer.RenderStillPng(Spec(ShareCardFormat.Story), 12.08, 0.5f, cts.Token));
    }
}
