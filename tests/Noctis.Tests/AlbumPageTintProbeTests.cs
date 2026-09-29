using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>End-to-end: a real-size PNG cover through AlbumDetailViewModel to the mounted
/// AlbumDetailView gradient Border. Pins the wiring the unit tests skip (they call ApplyTint directly).</summary>
public class AlbumPageTintProbeTests
{
    private readonly ITestOutputHelper _out;
    public AlbumPageTintProbeTests(ITestOutputHelper o) => _out = o;

    private sealed class FakeLastFm : ILastFmService
    {
        public bool IsAuthenticated => false;
        public string? Username => null;
        public void Configure(string? sessionKey) { }
        public Task<string> GetAuthUrlAsync() => Task.FromResult(string.Empty);
        public Task<bool> CompleteAuthAsync() => Task.FromResult(false);
        public string? GetSessionKey() => null;
        public void Logout() { }
        public Task ScrobbleAsync(Track track, DateTime startedAt) => Task.CompletedTask;
        public Task UpdateNowPlayingAsync(Track track) => Task.CompletedTask;
        public Task<string?> GetAlbumDescriptionAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetAlbumDescriptionFullAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAlbumDescriptionOverrideAsync(string a, string b, string? d, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAlbumDescriptionOverrideAsync(string a, string b, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    [AvaloniaFact]
    public void MountedPage_TintLandsFromFullSizePngCover()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var album = new Album { Id = Guid.NewGuid(), Name = "A", Artist = "B", Tracks = new List<Track>() };
        for (var i = 1; i <= 3; i++)
            album.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(),
                FilePath = TestPaths.Primary("tint", "A", $"{i:00} Song {i}.flac"),
                Title = $"Song {i}", Artist = "B", AlbumArtist = "B", Album = "A", TrackNumber = i,
            });
        var artPath = persistence.GetArtworkPath(album.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(artPath)!);
        using (var bmp = new SKBitmap(1500, 1500)) // real-library size, PNG under a .jpg name
        {
            using (var c = new SKCanvas(bmp)) c.Clear(new SKColor(0xF2, 0xC1, 0xD1));
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 90);
            using var fs = File.Create(artPath); data.SaveTo(fs);
        }
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        settings.AlbumPageTintEnabled = true; // default is off; this probe exercises the tint itself
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm(), settings);
        Assert.Equal(artPath, vm.HeaderArtPath);
        var view = new AlbumDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        for (var i = 0; i < 40 && vm.BackgroundBrush == null; i++) { Thread.Sleep(50); Dispatcher.UIThread.RunJobs(); }
        var bg = view.FindControl<Border>("AlbumTintBg")!;
        // The Border reaches Opacity 1 through a 0.16s transition whose headless clock is
        // not what this test pins; the code-behind's target is the base value.
        var baseOpacity = bg.GetBaseValue(Visual.OpacityProperty);
        _out.WriteLine($"vm.BackgroundBrush={vm.BackgroundBrush} border.Background={bg.Background} border.Opacity={bg.Opacity} base={baseOpacity} IsLightTint={vm.IsLightTint}");
        Assert.NotNull(vm.BackgroundBrush);
        Assert.NotNull(bg.Background);
        Assert.Equal(1, baseOpacity);
        Assert.True(vm.IsLightTint);

        // iTunes-style: the flat panel covers the album block only and stops above the
        // related sections (Other Versions / More By keep the app background).
        var related = view.FindControl<StackPanel>("RelatedSections")!;
        var tintBottom = bg.TranslatePoint(new Point(0, bg.Bounds.Height), view)!.Value.Y;
        var relatedTop = related.TranslatePoint(new Point(0, 0), view)!.Value.Y;
        _out.WriteLine($"tint panel bottom={tintBottom} related top={relatedTop} tint height={bg.Bounds.Height}");
        Assert.True(bg.Bounds.Height > 300, "panel must span the header");

        // Light cover → dark page text, and it must reach the parts that used to reset it:
        // the description (inside a Button) and the track rows (inside a ListBox).
        var dark = Color.FromRgb(0x11, 0x11, 0x11);
        var rowTitle = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("one-line-explicit-title"));
        var description = view.FindControl<TextBlock>("AlbumDescriptionText")!;
        _out.WriteLine($"row title fg={rowTitle.Foreground} description fg={description.Foreground}");
        Assert.Equal(dark, ((ISolidColorBrush)rowTitle.Foreground!).Color);
        Assert.Equal(dark, ((ISolidColorBrush)description.Foreground!).Color);
        // (Hidden when there are no related albums — then nothing follows the panel.)
        Assert.True(!related.IsVisible || tintBottom <= relatedTop + 0.5, "panel must end where the related sections begin");

        // Discord NJZ 09-22: a short tinted album fills the viewport (no strip of app
        // background under it) and the footer facts sit at the foot of the colour, above
        // the floating player bar's clearance.
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        var scroller = view.FindControl<ScrollViewer>("TrackScrollViewer")!;
        var block = view.FindControl<Grid>("AlbumBlock")!;
        _out.WriteLine($"viewport={scroller.Viewport.Height} block={block.Bounds.Height} related visible={related.IsVisible}");
        Assert.True(scroller.Viewport.Height > 300);
        Assert.True(block.Bounds.Height >= scroller.Viewport.Height - 0.5, "tinted block must reach the bottom of the viewport");
        Assert.False(related.IsVisible, "no related albums → no related block");
        var footer = view.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Text?.Contains("songs,") == true);
        var footerBottom = footer.TranslatePoint(new Point(0, footer.Bounds.Height), block)!.Value.Y;
        _out.WriteLine($"footer bottom={footerBottom} block bottom={block.Bounds.Height}");
        Assert.InRange(block.Bounds.Height - footerBottom, 100, 160); // bar clearance, not mid-page

        // With the accent following the cover, an accent-coloured artist name vanished into
        // the tint; on a tint the credit takes the page text colour.
        var credit = view.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Classes.Contains("artist-credit-text") && t.Text == "B");
        Assert.Equal(dark, ((ISolidColorBrush)credit.Foreground!).Color);
    }

    /// <summary>Discord "Album Page Redesign" (Jafezy mockup): with More By below a short
    /// album, the tint runs under the related rows (Tint whole page, on by default) and they
    /// follow the tracks directly — no viewport-tall stretch of empty colour first. Turning
    /// the option off restores the album-block-only tint.</summary>
    [AvaloniaFact]
    public void WholePageTint_CoversRelatedRows_WithoutViewportPadding()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var album = new Album { Id = Guid.NewGuid(), Name = "A", Artist = "B", Tracks = new List<Track>() };
        for (var i = 1; i <= 3; i++)
            album.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(),
                FilePath = TestPaths.Primary("tint-whole", "A", $"{i:00} Song {i}.flac"),
                Title = $"Song {i}", Artist = "B", AlbumArtist = "B", Album = "A", TrackNumber = i,
            });
        lib.ArtistAlbums.Add(new Album { Id = Guid.NewGuid(), Name = "Other", Artist = "B", Year = 2020, Tracks = new List<Track>() });
        var artPath = persistence.GetArtworkPath(album.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(artPath)!);
        using (var bmp = new SKBitmap(600, 600))
        {
            using (var c = new SKCanvas(bmp)) c.Clear(new SKColor(0xF2, 0xC1, 0xD1));
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 90);
            using var fs = File.Create(artPath); data.SaveTo(fs);
        }
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        settings.AlbumPageTintEnabled = true;
        Assert.True(settings.AlbumPageTintWholePage, "whole-page tint ships on");
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm(), settings);
        var view = new AlbumDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        for (var i = 0; i < 40 && vm.BackgroundBrush == null; i++) { Thread.Sleep(50); Dispatcher.UIThread.RunJobs(); }
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();

        Assert.NotNull(vm.BackgroundBrush);
        Assert.True(vm.HasMoreByArtist);
        var bg = view.FindControl<Border>("AlbumTintBg")!;
        var related = view.FindControl<StackPanel>("RelatedSections")!;
        var scroller = view.FindControl<ScrollViewer>("TrackScrollViewer")!;
        var footer = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text?.Contains("songs,") == true);
        double Top(Visual v) => v.TranslatePoint(default, view)!.Value.Y;
        double Bottom(Visual v) => v.TranslatePoint(new Point(0, v.Bounds.Height), view)!.Value.Y;

        _out.WriteLine($"whole: footer bottom={Bottom(footer)} related top={Top(related)} related bottom={Bottom(related)} tint bottom={Bottom(bg)} viewport={scroller.Viewport.Height}");
        Assert.True(Top(related) - Bottom(footer) < 80, "related rows must follow the tracks, not a viewport of padding");
        Assert.True(Bottom(bg) >= Bottom(related) - 0.5, "the tint must run under the related rows");
        Assert.True(Bottom(bg) >= scroller.Viewport.Height - 0.5, "the tint must still reach the bottom of the viewport");
        var moreByTitle = related.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == vm.MoreByArtistTitle);
        var tileTitle = related.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("page-text") && t.Inlines?.Count > 0);
        var dark = Color.FromRgb(0x11, 0x11, 0x11);
        Assert.Equal(dark, ((ISolidColorBrush)moreByTitle.Foreground!).Color);
        Assert.Equal(dark, ((ISolidColorBrush)tileTitle.Foreground!).Color);

        settings.AlbumPageTintWholePage = false;
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        _out.WriteLine($"block only: related top={Top(related)} tint bottom={Bottom(bg)}");
        Assert.True(Bottom(bg) <= Top(related) + 0.5, "off: the tint stops above the related rows");
        Assert.True(Bottom(bg) >= scroller.Viewport.Height - 0.5, "off: the album block still fills the viewport");
        Assert.NotEqual(dark, ((ISolidColorBrush)moreByTitle.Foreground!).Color);
        win.Close();
    }
}
