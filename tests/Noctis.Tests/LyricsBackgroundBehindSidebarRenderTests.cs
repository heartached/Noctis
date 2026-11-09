using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #184, rendered: the lyrics page hosted in the REAL MainWindow (PageHost inside
/// RootPanel, the sidebar rail over the window's left edge). With Background Behind the
/// Sidebar on, the page's backdrop must be drawn left of the page, under and around the rail,
/// not just laid out there. Real Skia only (NOCTIS_TEST_SKIA=1).
/// </summary>
public class LyricsBackgroundBehindSidebarRenderTests
{
    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private static readonly Color Probe = Color.FromRgb(0xE0, 0x10, 0x10);

    private static WriteableBitmap Shot(Window w)
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        using var frame = w.CaptureRenderedFrame()!;
        using var ms = new MemoryStream();
        frame.Save(ms);
        var dir = Environment.GetEnvironmentVariable("NOCTIS_184_SHOTS");
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, $"bleed-{Guid.NewGuid():N}.png"));
        }
        ms.Position = 0;
        return WriteableBitmap.Decode(ms);
    }

    private static Color At(WriteableBitmap b, int x, int y)
    {
        using var l = b.Lock();
        var px = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(l.Address + y * l.RowBytes + x * 4, px, 0, 4);
        return Color.FromRgb(px[2], px[1], px[0]); // BGRA
    }

    private static bool IsProbe(Color c) => c.R > 180 && c.G < 60 && c.B < 60;

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Backdrop_IsDrawnLeftOfThePage_OnlyWhenOn(bool on)
    {
        if (!HeadlessTestApp.RealRendering) Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        AddToPlaylistDialogTests.EnsureAppStyles();

        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            LyricsCenterWhenNoLyrics = false,
            LyricsBackgroundBehindSidebar = on,
        };
        var vm = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new MetadataService(),
            new TestPersistenceService(), new FakeLibraryService())
        {
            ShowLyricsSearchDialog = _ => Task.CompletedTask,
            ExtraLyricsSources = Array.Empty<Services.Lyrics.OnlineLyricsSource>(),
        };

        var main = new MainWindow { Width = 1200, Height = 800 };
        main.Show();
        try
        {
            var host = main.FindControl<ContentControl>("PageHost")!;
            var view = new LyricsView { DataContext = vm };
            host.Content = view;
            Dispatcher.UIThread.RunJobs();
            // A colour background the probe can find: the solid fill every mode paints under.
            vm.IsColorModeArtwork = false;
            vm.FullBackgroundBrush = new SolidColorBrush(Probe);

            using var shot = Shot(main);
            var scale = shot.PixelSize.Width / main.Bounds.Width;
            int Px(double v) => (int)(v * scale);
            var pageLeft = view.TranslatePoint(new Point(0, 0), main)!.Value.X;
            Assert.True(pageLeft > 60, $"page starts at {pageLeft}");

            // Inside the page: the colour in both cases.
            Assert.True(IsProbe(At(shot, Px(pageLeft + 40), Px(400))), $"page {At(shot, Px(pageLeft + 40), Px(400))}");
            // Left of the page: in the rail's left margin (x=3) and between rail and page.
            foreach (var x in new[] { 3.0, pageLeft - 4 })
            {
                var c = At(shot, Px(x), Px(400));
                Assert.True(IsProbe(c) == on, $"x={x} on={on} pixel={c}");
            }

            // Turning it off draws the page's edge where it was, at once.
            player.LyricsBackgroundBehindSidebar = false;
            using var after = Shot(main);
            Assert.False(IsProbe(At(after, Px(3), Px(400))), $"after off {At(after, Px(3), Px(400))}");
            Assert.True(IsProbe(At(after, Px(pageLeft + 40), Px(400))));
        }
        finally { main.Close(); }
    }
}
