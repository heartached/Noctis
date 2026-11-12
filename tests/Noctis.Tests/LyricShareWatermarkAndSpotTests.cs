using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using SkiaSharp;
using Xunit;
using static Noctis.Tests.LyricShareDialogTests;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-12: "on the share lyrics card, add an option to remove the noctis watermark. also
/// when unclick the follow playback and close reopen the share lyrics card, it should save the
/// spot and stay like that."
/// </summary>
public class LyricShareWatermarkAndSpotTests
{
    // ── The card without the wordmark ──

    /// <summary>Pixels a white wordmark (or white text) lights on a black card.</summary>
    private static int Bright(SKBitmap bmp)
    {
        int n = 0;
        foreach (var c in bmp.Pixels)
            if ((c.Red * 2126 + c.Green * 7152 + c.Blue * 722) / 10000 > 200) n++;
        return n;
    }

    /// <summary>A black card whose only white is the wordmark: no title, no artist, a "." for lyrics.</summary>
    private static LyricCardSpec Bare(ShareCardFormat format, ShareCardLayout layout, bool wordmark) => new()
    {
        Title = "", Artist = "", Lines = new[] { "." }, Format = format, Layout = layout,
        Background = ShareBackground.Solid, SolidColorHex = "#000000", TextColor = ShareTextColor.White,
        ShowWordmark = wordmark,
    };

    public static IEnumerable<object[]> Cards() =>
        from f in new[] { ShareCardFormat.Square, ShareCardFormat.Story }
        from l in new[] { ShareCardLayout.Panel, ShareCardLayout.Poster }
        select new object[] { f, l };

    /// <summary>Every card format and layout draws the wordmark by default and leaves it out
    /// when the option is off: the still (Save Card, Copy, the plain clip's frame).</summary>
    [Theory]
    [MemberData(nameof(Cards))]
    public void StillCard_DrawsTheWordmark_OnlyWhenOn(ShareCardFormat format, ShareCardLayout layout)
    {
        using var on = SKBitmap.Decode(ShareCardRenderer.RenderLyricCardStyled(Bare(format, layout, true)));
        using var off = SKBitmap.Decode(ShareCardRenderer.RenderLyricCardStyled(Bare(format, layout, false)));
        int withMark = Bright(on), without = Bright(off);
        Assert.True(withMark > 1500, $"{format} {layout}: the wordmark lit only {withMark} px");
        Assert.True(without < 400, $"{format} {layout}: {without} px still lit with the wordmark off");
    }

    /// <summary>The animated preview and the karaoke clip frames come from the same flag.</summary>
    [Theory]
    [MemberData(nameof(Cards))]
    public void KaraokePreviewAndClipFrames_LeaveTheWordmarkOut(ShareCardFormat format, ShareCardLayout layout)
    {
        var karaoke = new[] { new KaraokeLine { StartSeconds = 1, Words = new[] { new KaraokeWord(".", 1, 2) } } };

        int Animator(bool wordmark)
        {
            using var animator = new ShareCardRenderer.KaraokeCardAnimator(Bare(format, layout, wordmark), karaoke, 1f);
            var pixels = new byte[animator.PixelWidth * animator.PixelHeight * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                animator.RenderFrame(0.5, handle.AddrOfPinnedObject(), animator.PixelWidth * 4);
                using var bmp = new SKBitmap(new SKImageInfo(animator.PixelWidth, animator.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
                return Bright(bmp);
            }
            finally { handle.Free(); }
        }

        int Clip(bool wordmark)
        {
            var dir = Directory.CreateTempSubdirectory("noctis-wordmark-frames-").FullName;
            try
            {
                ShareCardRenderer.RenderKaraokeFrames(Bare(format, layout, wordmark), karaoke,
                    new ShareClipTiming(0.5, 0.05), 20, dir);
                using var frame = SKBitmap.Decode(Path.Combine(dir, "frame-00000.jpg"));
                return Bright(frame);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        Assert.True(Animator(true) > 1500, "the preview animator drew no wordmark with it on");
        Assert.True(Animator(false) < 400, "the preview animator drew the wordmark with it off");
        Assert.True(Clip(true) > 1500, "the clip frame drew no wordmark with it on");
        Assert.True(Clip(false) < 400, "the clip frame drew the wordmark with it off");
    }

    /// <summary>Rows of <paramref name="bmp"/> at column <paramref name="x"/> brighter than <paramref name="luma"/>: first and last.</summary>
    private static (int Top, int Bottom) LitRows(SKBitmap bmp, int luma, int? x = null)
    {
        int top = -1, bottom = -1;
        for (int y = 0; y < bmp.Height; y++)
        {
            bool lit = false;
            for (int cx = x ?? 0; cx < (x + 1 ?? bmp.Width); cx++)
            {
                var c = bmp.GetPixel(cx, y);
                if ((c.Red * 2126 + c.Green * 7152 + c.Blue * 722) / 10000 > luma) { lit = true; break; }
            }
            if (!lit) continue;
            if (top < 0) top = y;
            bottom = y;
        }
        return (top, bottom);
    }

    private static LyricCardSpec Short(ShareCardFormat format, ShareCardLayout layout, bool wordmark) => new()
    {
        Title = "TUTU", Artist = "6ix9ine", Lines = new[] { "HOLD ME", "CLOSER NOW" }, Format = format, Layout = layout,
        Background = ShareBackground.Solid, SolidColorHex = "#000000", TextColor = ShareTextColor.White,
        ShowWordmark = wordmark,
    };

    /// <summary>
    /// No empty band where the wordmark was. Card (Panel): the box hugs the lyrics, so it is
    /// shorter by the band (60 px gap + 44 px logo) and, centred in the same margins, starts half
    /// that lower. Poster: the block (artwork to lyrics) is centred again without the band, so the
    /// artwork moves down half of it (64 px gap + 44 px logo).
    /// </summary>
    [Theory]
    [MemberData(nameof(Cards))]
    public void WithoutTheWordmark_TheLayoutCloses_TheBand(ShareCardFormat format, ShareCardLayout layout)
    {
        using var on = SKBitmap.Decode(ShareCardRenderer.RenderLyricCardStyled(Short(format, layout, true)));
        using var off = SKBitmap.Decode(ShareCardRenderer.RenderLyricCardStyled(Short(format, layout, false)));
        if (layout == ShareCardLayout.Panel)
        {
            // Column 100 crosses only the box (its faint fill and edge), left of every text.
            var boxOn = LitRows(on, 10, 100);
            var boxOff = LitRows(off, 10, 100);
            int shrink = (boxOn.Bottom - boxOn.Top) - (boxOff.Bottom - boxOff.Top);
            Assert.InRange(shrink, 100, 108);
            Assert.InRange(boxOff.Top - boxOn.Top, 50, 54);
            // The box's margins stay balanced: centred in the format's safe band.
            int marginTop = format == ShareCardFormat.Story ? 240 : 60;
            int marginBottom = format == ShareCardFormat.Story ? 260 : 60;
            Assert.InRange((boxOff.Top - marginTop) - (off.Height - marginBottom - 1 - boxOff.Bottom), -3, 3);
            // The lyrics end near the box's bottom: no wordmark-sized gap below them.
            var text = LitRows(off, 200);
            int pad = format == ShareCardFormat.Story ? 44 : 64;
            Assert.True(boxOff.Bottom - text.Bottom < pad + 50, $"{boxOff.Bottom - text.Bottom} px under the lyrics");
        }
        else
        {
            var artOn = LitRows(on, 20, on.Width / 2);
            var artOff = LitRows(off, 20, off.Width / 2);
            Assert.InRange(artOff.Top - artOn.Top, 52, 56);
        }
    }

    /// <summary>Safe zones stay right without the wordmark: eight long lines keep inside the
    /// format's band, top and bottom (Story: y 240..1660, the Reels/TikTok overlays).</summary>
    [Theory]
    [InlineData(ShareCardFormat.Square, ShareCardLayout.Panel, 60, 60)]
    [InlineData(ShareCardFormat.Square, ShareCardLayout.Poster, 72, 72)]
    [InlineData(ShareCardFormat.Story, ShareCardLayout.Panel, 240, 260)]
    [InlineData(ShareCardFormat.Story, ShareCardLayout.Poster, 240, 260)]
    public void WithoutTheWordmark_LongSelections_StayInTheSafeBand(ShareCardFormat format, ShareCardLayout layout, int marginTop, int marginBottom)
    {
        var lines = Enumerable.Range(1, 8)
            .Select(i => $"You can't do this 'cause you ain't me, it drives you crazy every single night, line {i}").ToArray();
        var spec = new LyricCardSpec
        {
            Title = "TUTU", Artist = "6ix9ine", Lines = lines, Format = format, Layout = layout,
            Background = ShareBackground.Solid, SolidColorHex = "#000000", TextColor = ShareTextColor.White,
            ShowWordmark = false,
        };
        using var bmp = SKBitmap.Decode(ShareCardRenderer.RenderLyricCardStyled(spec));
        var (top, bottom) = LitRows(bmp, 90);
        Assert.True(top >= marginTop - 4, $"text from y={top}, above the band's {marginTop}");
        Assert.True(bottom <= bmp.Height - marginBottom + 4, $"text down to y={bottom}, below the band's {bmp.Height - marginBottom}");
    }

    // ── The dialog's option ──

    /// <summary>Settings kept in memory; counts saves.</summary>
    private sealed class MemorySettings : TestPersistenceService
    {
        public AppSettings Settings { get; } = new();
        public override Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);
        public override Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
    }

    private sealed class Services(IPersistenceService persistence) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IPersistenceService) ? persistence : null;
    }

    private static Track Bare() => new() { Id = Guid.NewGuid(), Title = "", Artist = "" };

    /// <summary>The option reaches the preview at once, Save Card / Copy's export render, and
    /// the spec the clip is drawn from; it is remembered, and the next dialog opens with it.</summary>
    [AvaloniaFact]
    public async Task WatermarkOption_ReachesEveryOutput_AndIsRemembered()
    {
        var previous = App.Services;
        using var settings = new MemorySettings();
        App.Services = new Services(settings);
        // A black card with white text, its only lyric a ".": what lights up is the wordmark.
        var vm = new LyricShareViewModel(Bare(), new[] { "." }, 0) { SolidColorHex = "#000000", TextColorIndex = 1 };
        try
        {
            Assert.True(vm.ShowWordmark);
            Assert.True(PumpUntil(() => vm.Preview != null && vm.IsPreviewCurrent, 10000), "preview never rendered");
            int on;
            using (var png = SKBitmap.Decode(await vm.RenderExportPngAsync()))
                on = Bright(png);
            Assert.True(on > 4 * 1500, $"the export has no wordmark with the option on ({on} px)");

            var before = vm.Preview;
            vm.ShowWordmark = false;
            Assert.False(vm.CardSpec.ShowWordmark);
            Assert.True(PumpUntil(() => vm.IsPreviewCurrent && !ReferenceEquals(vm.Preview, before), 5000),
                "the preview was not redrawn");
            using (var png = SKBitmap.Decode(await vm.RenderExportPngAsync()))
            {
                int off = Bright(png);
                Assert.True(off < 4 * 400, $"the export still has the wordmark with the option off ({off} of {on} px)");
            }
            Assert.True(PumpUntil(() => !settings.Settings.ShareLyricsShowWordmark), "the choice was not saved");

            vm.ShowWordmark = true;
            Assert.True(PumpUntil(() => settings.Settings.ShareLyricsShowWordmark), "turning it back on was not saved");
        }
        finally
        {
            vm.Detach();
            App.Services = previous;
        }

        var again = new LyricShareViewModel(Bare(), new[] { "." }, null, null, null, null, 0, null,
            new LyricShareOpenState { ShowWordmark = false });
        try { Assert.False(again.CardSpec.ShowWordmark); }
        finally { again.Detach(); }
    }

    /// <summary>Both remembered options survive a restart: written by one PersistenceService,
    /// read back by a new one from settings.json.</summary>
    [Fact]
    public async Task ShareOptions_RoundTripThroughSettingsJson()
    {
        var root = Directory.CreateTempSubdirectory("noctis-share-settings-").FullName;
        try
        {
            Assert.True(new AppSettings().ShareLyricsShowWordmark);
            Assert.True(new AppSettings().ShareLyricsFollowPlayback);
            await new PersistenceService(root).UpdateSettingsAsync(s =>
            {
                s.ShareLyricsShowWordmark = false;
                s.ShareLyricsFollowPlayback = false;
            });
            var loaded = await new PersistenceService(root).LoadSettingsAsync();
            Assert.False(loaded.ShareLyricsShowWordmark);
            Assert.False(loaded.ShareLyricsFollowPlayback);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ── Follow playback, remembered ──

    private static (PlayerViewModel Player, Track Song) Playing(int seconds)
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var song = new Track { Id = Guid.NewGuid(), Title = "A", Artist = "B" };
        player.CurrentTrack = song;
        player.Position = TimeSpan.FromSeconds(seconds);
        return (player, song);
    }

    private static List<string> Lines(int n) => Enumerable.Range(0, n).Select(i => $"line {i}").ToList();
    private static List<TimeSpan?> Stamps(int n) => Enumerable.Range(0, n).Select(i => (TimeSpan?)TimeSpan.FromSeconds(i * 10)).ToList();

    /// <summary>
    /// Unticking the box, or picking a line (which takes over from playback), is the user
    /// turning following off, and is saved; ticking it again is saved too. The Lyrics Page
    /// turning it off by itself is not the user's choice, and a song without synced lyrics
    /// (no Follow playback box) never writes it.
    /// </summary>
    [AvaloniaFact]
    public void FollowPlayback_Choice_IsSaved_OnlyWhenTheUserMakesIt()
    {
        var previous = App.Services;
        using var settings = new MemorySettings();
        App.Services = new Services(settings);
        var (player, song) = Playing(25);
        try
        {
            var vm = new LyricShareViewModel(song, Lines(20), Stamps(20), player);
            Assert.True(vm.SyncEnabled);
            vm.SyncEnabled = false;
            Assert.True(PumpUntil(() => !settings.Settings.ShareLyricsFollowPlayback), "unticking was not saved");
            vm.SyncEnabled = true;
            Assert.True(PumpUntil(() => settings.Settings.ShareLyricsFollowPlayback), "ticking again was not saved");
            vm.ToggleLine(vm.Lines[12]);    // a pick takes over from playback
            Assert.False(vm.SyncEnabled);
            Assert.True(PumpUntil(() => !settings.Settings.ShareLyricsFollowPlayback), "a pick turning it off was not saved");
            vm.Detach();

            settings.Settings.ShareLyricsFollowPlayback = true;
            var page = new LyricShareViewModel(song, Lines(20), Stamps(20), player);
            page.LayoutIndex = 2;            // the Lyrics Page stops following by itself
            Assert.True(page.IsPageLayout);
            Assert.False(page.SyncEnabled);
            PumpUntil(() => false, 100);
            Assert.True(settings.Settings.ShareLyricsFollowPlayback);
            page.Detach();

            settings.Settings.ShareLyricsFollowPlayback = false;
            var plain = new LyricShareViewModel(song, Lines(20), null, player);
            Assert.False(plain.SyncAvailable);
            plain.ClearSelectionCommand.Execute(null);
            plain.ToggleLine(plain.Lines[3]);
            plain.Detach();
            settings.Settings.ShareLyricsFollowPlayback = true;
            var plain2 = new LyricShareViewModel(song, Lines(20), null, player);
            plain2.ToggleLine(plain2.Lines[3]);
            plain2.Detach();
            PumpUntil(() => false, 100);
            Assert.True(settings.Settings.ShareLyricsFollowPlayback);   // untouched by unsynced songs
        }
        finally { App.Services = previous; }
    }

    /// <summary>Opening with following off: the box is unticked, playback does not move the
    /// picks, and ticking it follows again at once.</summary>
    [AvaloniaFact]
    public void OpeningWithFollowOff_KeepsThePicks_UntilTickedAgain()
    {
        var (player, song) = Playing(52);
        var vm = new LyricShareViewModel(song, Lines(20), Stamps(20), player, preselectIndex: 5,
            open: new LyricShareOpenState { FollowPlayback = false });
        try
        {
            Assert.False(vm.SyncEnabled);
            Assert.Equal(new[] { 5, 6, 7, 8 }, vm.SelectedIndices);
            Assert.Equal(5, vm.InitialScrollIndex);
            player.Position = TimeSpan.FromSeconds(101);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { 5, 6, 7, 8 }, vm.SelectedIndices);

            vm.SyncEnabled = true;
            Assert.Equal(new[] { 10, 11, 12, 13 }, vm.SelectedIndices);
            player.Position = TimeSpan.FromSeconds(121);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { 12, 13, 14, 15 }, vm.SelectedIndices);
        }
        finally { vm.Detach(); }
    }

    // ── The spot, per song ──

    private static async Task<LongLyricsWindowTests.Loaded> Load(int lines)
    {
        var l = await LongLyricsWindowTests.LoadSidecar(LongLyricsWindowTests.BookLrc(lines));
        return l;
    }

    private static void PlayLine(LongLyricsWindowTests.Loaded l, int number)
    {
        var at = LongLyricsWindowTests.LineStart(number) + TimeSpan.FromMilliseconds(600);
        l.Player.Position = at;
        LongLyricsWindowTests.PlayAt(l.Vm, at);
    }

    private static readonly AppSettings FollowOff = new() { ShareLyricsFollowPlayback = false };

    private static List<string> PickedTexts(LyricShareViewModel vm) =>
        vm.Lines.Where(x => x.IsSelected).Select(x => x.Text.Split(" at ")[0]).ToList();

    /// <summary>Same song, following off: the lines picked at close are picked again, and the
    /// list opens on them, wherever playback is now.</summary>
    [AvaloniaFact]
    public async Task SameSong_FollowOff_PicksTheSameLinesAgain()
    {
        using var l = await Load(60);
        PlayLine(l, 10);
        var first = l.Vm.BuildShareViewModel(FollowOff)!;
        Assert.False(first.SyncEnabled);
        Assert.Equal(new[] { "Line 10", "Line 11", "Line 12", "Line 13" }, PickedTexts(first));
        first.ClearSelectionCommand.Execute(null);
        first.ToggleLine(first.Lines[40]);
        first.ToggleLine(first.Lines[42]);
        first.Detach();

        PlayLine(l, 25);
        var again = l.Vm.BuildShareViewModel(FollowOff)!;
        try
        {
            Assert.Equal(new[] { "Line 41", "Line 43" }, PickedTexts(again));
            Assert.Equal(40, again.InitialScrollIndex);
        }
        finally { again.Detach(); }
    }

    /// <summary>Another song with following off starts as the first open does: the playing
    /// line and the next three picked, and the list on them.</summary>
    [AvaloniaFact]
    public async Task AnotherSong_FollowOff_StartsAtThePlayingLine()
    {
        using var l = await Load(60);
        PlayLine(l, 10);
        var first = l.Vm.BuildShareViewModel(FollowOff)!;
        first.ClearSelectionCommand.Execute(null);
        first.ToggleLine(first.Lines[40]);
        first.Detach();

        // Another song with the very same lyrics (as a second copy of the file would be).
        l.Player.CurrentTrack!.Id = Guid.NewGuid();
        PlayLine(l, 20);
        var other = l.Vm.BuildShareViewModel(FollowOff)!;
        try
        {
            Assert.Equal(new[] { "Line 20", "Line 21", "Line 22", "Line 23" }, PickedTexts(other));
            Assert.Equal(19, other.InitialScrollIndex);
        }
        finally { other.Detach(); }
    }

    /// <summary>Lyrics that changed since (a line rewritten) drop the spot rather than pick other lines.</summary>
    [AvaloniaFact]
    public async Task ChangedLyrics_DropTheSpot()
    {
        using var l = await Load(60);
        PlayLine(l, 10);
        var first = l.Vm.BuildShareViewModel(FollowOff)!;
        first.ClearSelectionCommand.Execute(null);
        first.ToggleLine(first.Lines[40]);
        first.Detach();

        l.Vm.LyricLines[5].Text = "Line 6, corrected";
        var again = l.Vm.BuildShareViewModel(FollowOff)!;
        try { Assert.Equal(new[] { "Line 10", "Line 11", "Line 12", "Line 13" }, PickedTexts(again)); }
        finally { again.Detach(); }
    }

    /// <summary>Nothing changes for Follow playback on: the dialog follows playback, a spot
    /// kept from before is not used, and closing while following keeps none.</summary>
    [AvaloniaFact]
    public async Task FollowOn_IgnoresAndClearsTheSpot()
    {
        using var l = await Load(60);
        PlayLine(l, 10);
        var off = l.Vm.BuildShareViewModel(FollowOff)!;
        off.ClearSelectionCommand.Execute(null);
        off.ToggleLine(off.Lines[40]);
        off.Detach();

        PlayLine(l, 30);
        var on = l.Vm.BuildShareViewModel(new AppSettings())!;
        Assert.True(on.SyncEnabled);
        Assert.Equal(new[] { "Line 30", "Line 31", "Line 32", "Line 33" }, PickedTexts(on));
        on.Detach();

        var offAgain = l.Vm.BuildShareViewModel(FollowOff)!;
        try { Assert.Equal(new[] { "Line 30", "Line 31", "Line 32", "Line 33" }, PickedTexts(offAgain)); }
        finally { offAgain.Detach(); }
    }

    /// <summary>
    /// Windowed transcript (15,000 lines; the dialog gets 3,000 around playback): lines picked at
    /// 9,000 are picked again after playback moved to 14,000, out of the stretch it would hand
    /// the dialog now. The lines are kept by their place in the whole lyrics, and the stretch
    /// is taken around them.
    /// </summary>
    [AvaloniaFact]
    public async Task WindowedLyrics_PickTheSameLinesAgain_FarFromPlayback()
    {
        using var l = await Load(15000);
        Assert.True(l.Vm.IsLyricsWindowed);
        PlayLine(l, 9000);
        var first = l.Vm.BuildShareViewModel(FollowOff)!;
        Assert.Equal(new[] { "Line 9000", "Line 9001", "Line 9002", "Line 9003" }, PickedTexts(first));
        int i9100 = first.Lines.ToList().FindIndex(x => x.Text.StartsWith("Line 9100 at"));
        first.ClearSelectionCommand.Execute(null);
        first.ToggleLine(first.Lines[i9100]);
        first.ToggleLine(first.Lines[i9100 + 1]);
        first.Detach();

        PlayLine(l, 14000);
        var again = l.Vm.BuildShareViewModel(FollowOff)!;
        try
        {
            Assert.DoesNotContain(again.Lines, x => x.Text.StartsWith("Line 14000 at") && x.IsSelected);
            Assert.Equal(new[] { "Line 9100", "Line 9101" }, PickedTexts(again));
            Assert.Equal(again.Lines.ToList().FindIndex(x => x.Text.StartsWith("Line 9100 at")), again.InitialScrollIndex);
        }
        finally { again.Detach(); }

        // Following on there: the stretch is around playback, as before.
        var on = l.Vm.BuildShareViewModel(new AppSettings())!;
        try { Assert.Equal(new[] { "Line 14000", "Line 14001", "Line 14002", "Line 14003" }, PickedTexts(on)); }
        finally { on.Detach(); }
    }

    // ── The dialog ──

    /// <summary>
    /// The box: above the card preview, level with the lines' header (the list and the well start
    /// together), not squeezed, hidden for the Lyrics Page (no wordmark there), and bound to the
    /// option. The card keeps its designed size, so at every Interface Size (60%-200%) and in the
    /// smallest main window (780×500) and a large one the inner layout is the same.
    /// Also the list opens on the picks: the first scroll request came before the dialog listened.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(0.6, 780, 500)]
    [InlineData(1.0, 780, 500)]
    [InlineData(1.0, 1920, 1030)]
    [InlineData(1.5, 1920, 1030)]
    [InlineData(2.0, 1920, 1030)]
    [InlineData(2.0, 780, 500)]
    public void WatermarkBox_SitsAboveThePreview_AndTheListOpensOnThePicks(double scale, int width, int height)
    {
        EnsureAppStyles();
        Noctis.Helpers.UiScale.Install();
        Noctis.Helpers.UiScale.SetFactor(scale);
        var lines = Enumerable.Range(0, 40).Select(i => $"line {i}").ToList();
        var stamps = Enumerable.Range(0, 40).Select(i => (TimeSpan?)TimeSpan.FromSeconds(i * 2)).ToList();
        var vm = new LyricShareViewModel(new Track { Id = Guid.NewGuid(), Title = "T", Artist = "A" }, lines, stamps, null, preselectIndex: 30);
        var win = new Noctis.Views.LyricShareDialog(vm) { Width = width, Height = height };
        win.Show();
        try
        {
            var host = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win).OfType<Noctis.Controls.PillDialogHost>().Single();
            Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 }), "open animation never settled");
            var box = win.FindControl<CheckBox>("WatermarkBox")!;
            var well = win.FindControl<Border>("PreviewWell")!;
            var list = win.FindControl<ItemsControl>("LineItems")!;
            var scroller = (ScrollViewer)list.Parent!;
            Assert.True(PumpUntil(() => box.Bounds.Width > 0 && well.Bounds.Height > 0));

            Assert.True(box.IsEffectivelyVisible);
            Assert.True(box.IsChecked);
            Assert.True(box.DesiredSize.Width <= box.Bounds.Width + 0.5, "the box is squeezed");
            var card = host.Card!;
            Point At(Visual v) => v.TranslatePoint(default, card)!.Value;
            // Inside the preview's column, above the well, which starts level with the list.
            Assert.True(At(box).X >= At(well).X - 0.5 && At(box).X + box.Bounds.Width <= At(well).X + well.Bounds.Width + 0.5,
                "the box runs out of the preview's column");
            Assert.True(At(box).Y + box.Bounds.Height <= At(well).Y + 0.5, "the box overlaps the card preview");
            Assert.Equal(At(scroller).Y, At(well).Y, 1);
            Assert.True(well.Bounds.Height >= 300, $"the preview well shrank to {well.Bounds.Height:0} px");

            box.IsChecked = false;
            Assert.False(vm.ShowWordmark);
            box.IsChecked = true;
            Assert.True(vm.ShowWordmark);

            // The list opened on line 30, not at its top.
            Assert.True(PumpUntil(() => scroller.Offset.Y > 0, 2000), "the list opened at its top, the picks out of view");
            var row = list.ContainerFromIndex(30)!;
            var rowTop = row.TranslatePoint(default, scroller)!.Value.Y;
            Assert.InRange(rowTop, -0.5, scroller.Viewport.Height - row.Bounds.Height + 0.5);

            vm.LayoutIndex = 2;
            Assert.True(PumpUntil(() => !box.IsEffectivelyVisible), "the box stays for the Lyrics Page, which has no wordmark");
        }
        finally
        {
            if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
            vm.Detach();
            Noctis.Helpers.UiScale.SetFactor(1.0);
        }
    }
}
