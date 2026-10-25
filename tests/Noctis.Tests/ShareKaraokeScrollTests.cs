using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Share Lyrics karaoke card with Follow playback on (flow mode): the whole song is one list
/// that glides up like the lyrics page instead of a 4-line card rebuilt (re-fitted, resized,
/// fades restarted) on every line change.
/// </summary>
public class ShareKaraokeScrollTests
{
    // Line i starts at 5 + 5i s; line 6 is long enough to wrap.
    private const int SongLines = 12;
    private static string Text(int i) => i == 6
        ? "this one is a much longer lyric line that has to wrap across the card at least once"
        : $"line {i} here";

    private static double Start(int i) => 5 + 5 * i;

    private static IReadOnlyList<KaraokeLine> Karaoke() => Enumerable.Range(0, SongLines).Select(i =>
    {
        var tokens = Text(i).Split(' ');
        return new KaraokeLine
        {
            StartSeconds = Start(i),
            Words = tokens.Select((w, k) => new KaraokeWord(w, Start(i) + k * 0.25, Start(i) + (k + 1) * 0.25)).ToArray(),
        };
    }).ToArray();

    private static LyricCardSpec Spec(ShareCardLayout layout, ShareCardFormat format) => new()
    {
        Title = "Title", Artist = "Artist",
        Lines = Enumerable.Range(0, SongLines).Select(Text).ToArray(),
        Background = ShareBackground.Solid, SolidColorHex = "#101010", TextColor = ShareTextColor.White,
        Layout = layout, Format = format,
    };

    private sealed class Frame : IDisposable
    {
        private readonly ShareCardRenderer.KaraokeCardAnimator _a;
        private GCHandle _handle;
        public readonly byte[] Pixels;
        public Frame(ShareCardRenderer.KaraokeCardAnimator a)
        {
            _a = a;
            Pixels = new byte[a.PixelWidth * a.PixelHeight * 4];
            _handle = GCHandle.Alloc(Pixels, GCHandleType.Pinned);
        }
        public void Render(double t) => _a.RenderFrame(t, _handle.AddrOfPinnedObject(), _a.PixelWidth * 4);
        public void Dispose() => _handle.Free();
    }

    public static IEnumerable<object[]> Cards() =>
        from l in new[] { ShareCardLayout.Panel, ShareCardLayout.Poster }
        from f in new[] { ShareCardFormat.Square, ShareCardFormat.Story }
        select new object[] { l, f };

    [Theory]
    [MemberData(nameof(Cards))]
    public void Flow_glides_to_the_next_line_then_settles_on_the_page_anchor(ShareCardLayout layout, ShareCardFormat format)
    {
        using var a = new ShareCardRenderer.KaraokeCardAnimator(Spec(layout, format), Karaoke(), 0.5f, flow: true);
        using var frame = new Frame(a);
        Assert.True(a.IsFlow);
        var area = a.FlowArea!.Value;
        var size = a.LyricSize;
        var tops = a.RowTops.ToArray();

        var offsets = new List<(double T, int Line, double Offset, bool Gliding)>();
        for (var t = 12.0; t <= 23.0; t += 1 / 30.0)
        {
            frame.Render(t);
            offsets.Add((t, a.Scroll!.Line, a.ScrollOffset, a.Scroll.IsGliding));
        }

        // Settled on line 2 before line 3's lead, gliding after it, settled on line 3 at the end.
        var switchAt = offsets.FindIndex(o => o.Line == 3);
        Assert.True(switchAt > 0, "the scroll never moved to line 3");
        var before = offsets[switchAt - 1];
        Assert.Equal(2, before.Line);
        Assert.Equal(a.Scroll!.Target(2), before.Offset, 3);
        // The page's lead: the list sets off ScrollLeadMs before the line is sung.
        Assert.InRange(Start(3) - offsets[switchAt].T, 0.5, 0.75);

        var mid = offsets[switchAt + 3];
        Assert.True(mid.Gliding);
        Assert.InRange(mid.Offset, a.Scroll.Target(2) + 1, a.Scroll.Target(3) - 1);

        var end = offsets[^1];
        Assert.False(end.Gliding);
        Assert.Equal(a.Scroll.Target(3), end.Offset, 6);

        // The font and the geometry never changed over the line change.
        Assert.Equal(size, a.LyricSize);
        Assert.Equal(area, a.FlowArea!.Value);
        Assert.Equal(tops, a.RowTops);

        // On the page's anchor: line 3's middle at 0.32 of the lyric area; line 2 above it,
        // inside the area (visible, dimmed by the focus); line 4 below.
        float RowTopOnCard(int r) => (float)(area.Top + tops[r] - end.Offset);
        var rows3 = Enumerable.Range(0, tops.Length).Where(r => a.RowLines[r] == 3).ToArray();
        var middle = (RowTopOnCard(rows3.First()) + RowTopOnCard(rows3.Last()) + a.LineHeight) / 2;
        Assert.Equal(area.Top + area.Height * 0.32, middle, 0.5);
        var prev = Enumerable.Range(0, tops.Length).Single(r => a.RowLines[r] == 2);
        Assert.True(RowTopOnCard(prev) >= area.Top - 0.5, $"line 2 starts above the area ({RowTopOnCard(prev)} < {area.Top})");
        Assert.True(RowTopOnCard(prev) + a.LineHeight < RowTopOnCard(rows3.First()));
        var next = Enumerable.Range(0, tops.Length).Single(r => a.RowLines[r] == 4);
        Assert.True(RowTopOnCard(next) + a.LineHeight <= area.Bottom, "line 4 is out of the area");
        Assert.True(a.Flow.LineOpacity(2, end.T) < a.Flow.LineOpacity(3, end.T), "the previous line is not dimmed");
    }

    [Fact]
    public void Flow_seek_back_and_far_forward_snap_instead_of_gliding()
    {
        using var a = new ShareCardRenderer.KaraokeCardAnimator(
            Spec(ShareCardLayout.Panel, ShareCardFormat.Square), Karaoke(), 0.5f, flow: true);
        using var frame = new Frame(a);
        for (var t = 30.0; t <= 33.0; t += 1 / 30.0) frame.Render(t);

        frame.Render(7.0);   // back to line 0
        Assert.Equal(0, a.Scroll!.Line);
        Assert.False(a.Scroll.IsGliding);
        Assert.Equal(a.Scroll.Target(0), a.ScrollOffset, 6);

        frame.Render(49.0);  // forward to line 8 (> a frame gap)
        Assert.Equal(8, a.Scroll.Line);
        Assert.False(a.Scroll.IsGliding);
        Assert.Equal(a.Scroll.Target(8), a.ScrollOffset, 6);
    }

    [Fact]
    public void Flow_spaces_lines_apart_but_keeps_wrapped_rows_tight()
    {
        using var a = new ShareCardRenderer.KaraokeCardAnimator(
            Spec(ShareCardLayout.Panel, ShareCardFormat.Square), Karaoke(), 0.5f, flow: true);
        var tops = a.RowTops;
        var lines = a.RowLines;
        Assert.True(lines.Count(l => l == 6) >= 2, "line 6 should wrap");
        float? lineGap = null;
        for (var r = 1; r < tops.Count; r++)
        {
            var step = tops[r] - tops[r - 1];
            if (lines[r] == lines[r - 1])
                Assert.Equal(a.LineHeight, step, 2);
            else
            {
                Assert.True(step > a.LineHeight + 1, "no gap between two lyric lines");
                lineGap ??= step;
                Assert.Equal(lineGap.Value, step, 2);
            }
        }
    }

    [Fact]
    public void Flow_draws_only_inside_the_lyric_area()
    {
        using var a = new ShareCardRenderer.KaraokeCardAnimator(
            Spec(ShareCardLayout.Panel, ShareCardFormat.Square), Karaoke(), 0.5f, flow: true);
        using var frame = new Frame(a);
        var area = a.FlowArea!.Value;
        frame.Render(7.0);
        var early = (byte[])frame.Pixels.Clone();
        frame.Render(49.0);
        var late = frame.Pixels;

        var stride = a.PixelWidth * 4;
        int top = (int)Math.Floor(area.Top * 0.5f) - 1, bottom = (int)Math.Ceiling(area.Bottom * 0.5f) + 1;
        int changedInside = 0;
        for (var y = 0; y < a.PixelHeight; y++)
        for (var x = 0; x < stride; x++)
        {
            var i = y * stride + x;
            if (early[i] == late[i]) continue;
            Assert.InRange(y, top, bottom);   // nothing drawn over the header or footer
            changedInside++;
        }
        Assert.True(changedInside > 0, "the list did not move");
    }

    [Fact]
    public void Flow_lyric_size_does_not_depend_on_the_pick()
    {
        // The non-flow card re-fits the font to its pick; flow keeps one size for the song.
        var shortPick = new LyricCardSpec
        {
            Title = "Title", Artist = "Artist", Lines = new[] { "a", "b" },
            Background = ShareBackground.Solid, SolidColorHex = "#101010",
        };
        using var flowA = new ShareCardRenderer.KaraokeCardAnimator(
            Spec(ShareCardLayout.Panel, ShareCardFormat.Square), Karaoke(), 0.5f, flow: true);
        using var flowB = new ShareCardRenderer.KaraokeCardAnimator(
            shortPick, Karaoke().Take(2).ToArray(), 0.5f, flow: true);
        Assert.Equal(flowA.LyricSize, flowB.LyricSize);
        Assert.Equal(flowA.FlowArea, flowB.FlowArea);
    }

    [Fact]
    public void Export_frames_scroll_like_the_preview()
    {
        var spec = Spec(ShareCardLayout.Panel, ShareCardFormat.Square);
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-flow-frames-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            const int fps = 10;
            var timing = new ShareClipTiming(18.0, 3.0);
            ShareCardRenderer.RenderKaraokeFrames(spec, Karaoke(), timing, fps, dir, flow: true);
            var files = Directory.GetFiles(dir, "frame-*.jpg").OrderBy(f => f).ToArray();
            Assert.Equal(30, files.Length);

            // The same frames through the preview's animator at full scale.
            using var a = new ShareCardRenderer.KaraokeCardAnimator(spec, Karaoke(), 1f, flow: true);
            using var frame = new Frame(a);
            for (var i = 0; i < files.Length; i++)
                frame.Render(timing.StartSeconds + (double)i / fps + 0.08);

            using var jpeg = SKBitmap.Decode(files[^1]);
            Assert.Equal(a.PixelWidth, jpeg.Width);
            Assert.Equal(a.PixelHeight, jpeg.Height);
            long diff = 0;
            for (var y = 0; y < jpeg.Height; y += 2)
            for (var x = 0; x < jpeg.Width; x += 2)
            {
                var c = jpeg.GetPixel(x, y);
                var i = (y * a.PixelWidth + x) * 4;
                diff += Math.Abs(c.Blue - frame.Pixels[i]) + Math.Abs(c.Green - frame.Pixels[i + 1])
                        + Math.Abs(c.Red - frame.Pixels[i + 2]);
            }
            var mean = diff / (3.0 * (jpeg.Width / 2) * (jpeg.Height / 2));
            Assert.True(mean < 2.0, $"export frame differs from the preview (mean {mean:0.00})");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── Dialog ──

    private static LyricShareViewModel SyncedVm(PlayerViewModel player, Track song)
    {
        var lines = Enumerable.Range(0, SongLines).Select(Text).ToList();
        var stamps = lines.Select((_, i) => (TimeSpan?)TimeSpan.FromSeconds(Start(i))).ToList();
        var words = lines.Select((l, i) => (IReadOnlyList<WordTiming>?)l.Split(' ')
            .Select((w, k) => new WordTiming { Text = w, Start = TimeSpan.FromSeconds(Start(i) + k * 0.25) }).ToList()).ToList();
        return new LyricShareViewModel(song, lines, stamps, player, words);
    }

    [AvaloniaFact]
    public void Follow_playback_line_changes_scroll_the_card_without_rebuilding_it()
    {
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), persistence, new FakeAnimatedCoverService());
        var song = new Track { Id = Guid.NewGuid(), Title = "T", Artist = "A", FilePath = string.Empty };
        player.CurrentTrack = song;
        player.Position = TimeSpan.FromSeconds(11);
        var vm = SyncedVm(player, song);
        try
        {
            Assert.True(vm.SyncEnabled);
            Assert.True(vm.IsFlowMode);
            Assert.True(LyricShareDialogTests.PumpUntil(() => vm.Animator != null && vm.IsPreviewCurrent, 5000));
            var animator = vm.Animator!;
            var builds = vm.AnimatorBuilds;
            Assert.True(animator.IsFlow);
            var size = animator.LyricSize;
            var area = animator.FlowArea;

            foreach (var s in new[] { 16, 21, 26, 31 })
            {
                player.Position = TimeSpan.FromSeconds(s);
                LyricShareDialogTests.PumpUntil(() => false, 120);
            }
            // Follow still re-picks the lines (Copy / Save Card use them) ...
            Assert.True(vm.Lines[5].IsSelected);
            // ... but the card is the same animator: no re-fit, no resize, no restarted fades.
            Assert.Same(animator, vm.Animator);
            Assert.Equal(builds, vm.AnimatorBuilds);
            Assert.Equal(size, vm.Animator!.LyricSize);
            Assert.Equal(area, vm.Animator.FlowArea);

            // A real option change rebuilds it.
            vm.FormatIndex = 1;
            Assert.True(LyricShareDialogTests.PumpUntil(() => vm.AnimatorBuilds > builds && vm.IsPreviewCurrent, 5000));
            Assert.True(vm.Animator!.IsFlow);

            // Follow off: the picked lines, laid out as before (no flow).
            vm.SyncEnabled = false;
            var offBuilds = vm.AnimatorBuilds;
            Assert.True(LyricShareDialogTests.PumpUntil(() => vm.AnimatorBuilds > offBuilds && vm.IsPreviewCurrent, 5000));
            Assert.False(vm.IsFlowMode);
            Assert.False(vm.Animator!.IsFlow);
            Assert.Null(vm.Animator.FlowArea);
        }
        finally { vm.Detach(); }
    }
}
