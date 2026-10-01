using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #114: a click on the album page's cover opens it full size. The viewer fits the
/// cover inside the window, decodes it at exactly the size it is drawn (sharp, not the
/// header's 512px decode stretched) and lets the bitmap go when it closes.
/// </summary>
public class AlbumArtworkViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-viewer-" + Guid.NewGuid().ToString("N"));

    public AlbumArtworkViewerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

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

    private static void WritePng(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bmp = new SKBitmap(width, height);
        using (var c = new SKCanvas(bmp)) c.Clear(new SKColor(0x30, 0x60, 0x90));
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    private static AlbumDetailViewModel OpenPage(bool withCover, out string artPath)
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var album = new Album { Id = Guid.NewGuid(), Name = "A", Artist = "B", Tracks = new List<Track>() };
        artPath = persistence.GetArtworkPath(album.Id);
        if (withCover) WritePng(artPath, 600, 600);
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm());
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    // ── Fit ────────────────────────────────────────────────────────

    [Fact]
    public void Fit_SquareCover_IsLimitedByTheWindowHeight()
    {
        // 1000×800 window: 56px margins all round and 28px for the size caption → 888×660.
        var fit = ArtworkViewerDialog.FitArtwork(new PixelSize(3000, 3000), new Size(1000, 800), 1.0);
        Assert.Equal(new PixelSize(660, 660), fit);
    }

    [Fact]
    public void Fit_WideCover_IsLimitedByTheWindowWidth_AndKeepsItsAspect()
    {
        var fit = ArtworkViewerDialog.FitArtwork(new PixelSize(3000, 2000), new Size(1000, 800), 1.0);
        Assert.Equal(new PixelSize(888, 592), fit);
    }

    [Fact]
    public void Fit_SmallCover_IsEnlargedToTheSpace()
    {
        var fit = ArtworkViewerDialog.FitArtwork(new PixelSize(300, 300), new Size(1000, 800), 1.0);
        Assert.Equal(new PixelSize(660, 660), fit);
    }

    [Fact]
    public void Fit_IsInDevicePixels_AtTheWindowsScaling()
    {
        // 125%: the same window is 1110×825 device px of space, so the decode matches the screen 1:1.
        var fit = ArtworkViewerDialog.FitArtwork(new PixelSize(3000, 3000), new Size(1000, 800), 1.25);
        Assert.Equal(new PixelSize(825, 825), fit);
    }

    [Fact]
    public void Fit_NoSpaceOrNoSize_IsEmpty()
    {
        Assert.Equal(default, ArtworkViewerDialog.FitArtwork(new PixelSize(0, 0), new Size(1000, 800), 1.0));
        Assert.Equal(default, ArtworkViewerDialog.FitArtwork(new PixelSize(3000, 3000), new Size(100, 100), 1.0));
    }

    // ── Album page command ─────────────────────────────────────────

    [AvaloniaFact]
    public void NoCover_NothingToOpen()
    {
        var vm = OpenPage(withCover: false, out _);
        var opened = 0;
        vm.ShowArtworkViewer = _ => { opened++; return Task.CompletedTask; };

        Assert.False(vm.CanViewArtwork);
        Assert.False(vm.ViewArtworkCommand.CanExecute(null));
        vm.ViewArtworkCommand.Execute(null); // even run directly, there is nothing to show
        Assert.Equal(0, opened);
    }

    [AvaloniaFact]
    public void Cover_OpensTheViewerOnce_UntilItCloses()
    {
        var vm = OpenPage(withCover: true, out var artPath);
        var shown = new List<string>();
        var open = new TaskCompletionSource();
        vm.ShowArtworkViewer = path => { shown.Add(path); return open.Task; };

        Assert.True(vm.CanViewArtwork);
        vm.ViewArtworkCommand.Execute(null);
        Assert.Equal(new[] { artPath }, shown);

        // A second click (a double-click on the cover) while it is up opens no second viewer:
        // the page only runs the command while it can execute.
        Assert.False(vm.ViewArtworkCommand.CanExecute(null));
        Assert.Single(shown);

        open.SetResult();
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.ViewArtworkCommand.CanExecute(null));
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static Border HeaderCover(AlbumDetailView view) =>
        view.GetVisualDescendants().OfType<Border>().First(b => b.Width == 340 && b.Height == 340);

    [AvaloniaFact]
    public void MountedPage_ClickOnTheCover_OpensTheViewer()
    {
        EnsureAppStyles();
        var vm = OpenPage(withCover: true, out var artPath);
        var shown = new List<string>();
        vm.ShowArtworkViewer = path => { shown.Add(path); return Task.CompletedTask; };
        var view = new AlbumDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var cover = HeaderCover(view);
        Assert.Contains("cover-viewable", cover.Classes);
        Assert.NotNull(cover.Cursor); // the hand

        var centre = cover.TranslatePoint(new Point(170, 170), win)!.Value;
        win.MouseDown(centre, MouseButton.Left);
        win.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { artPath }, shown);
        win.Close();
    }

    [AvaloniaFact]
    public void MountedPage_NoCover_IsNotClickable()
    {
        EnsureAppStyles();
        var vm = OpenPage(withCover: false, out _);
        var shown = 0;
        vm.ShowArtworkViewer = _ => { shown++; return Task.CompletedTask; };
        var view = new AlbumDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var cover = HeaderCover(view);
        Assert.DoesNotContain("cover-viewable", cover.Classes);
        var centre = cover.TranslatePoint(new Point(170, 170), win)!.Value;
        win.MouseDown(centre, MouseButton.Left);
        win.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, shown);
        win.Close();
    }

    // ── Viewer window ──────────────────────────────────────────────

    private static void PumpUntil(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) { Thread.Sleep(20); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public void Viewer_DecodesAtTheDrawnSize_ShowsTheRealSize_AndReleasesOnClose()
    {
        var path = Path.Combine(_dir, "cover.jpg"); // the artwork store names PNGs .jpg too
        WritePng(path, 3000, 2000);

        var dialog = new ArtworkViewerDialog(path) { Width = 1000, Height = 800 };
        dialog.Show();
        PumpUntil(() => dialog.FullBitmap != null);

        // The headless drawing stubs don't keep a bitmap's pixel size, so pin the request:
        // the decode is asked for the drawn width (DecodeToWidth's own size is tested apart).
        var full = dialog.FullBitmap;
        Assert.NotNull(full);
        Assert.Equal(888, dialog.FullDecodeWidth);
        var frame = dialog.FindControl<Border>("ArtworkFrame")!;
        Assert.Equal(888, frame.Width);
        Assert.Equal(592, frame.Height);
        Assert.Same(full, dialog.FindControl<Image>("FullImage")!.Source);
        Assert.Equal("3000 × 2000", dialog.FindControl<TextBlock>("SizeText")!.Text);

        dialog.Close();
        Assert.Null(dialog.FullBitmap);
        Assert.Null(dialog.FindControl<Image>("FullImage")!.Source);
        Assert.True(IsDisposed(full!), "the full-size bitmap must be disposed on close");
    }

    private static bool IsDisposed(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        try { _ = bitmap.PixelSize; return false; }
        catch (ObjectDisposedException) { return true; }
        catch (NullReferenceException) { return true; }
    }

    [AvaloniaFact]
    public void Viewer_EscapeCloses()
    {
        var path = Path.Combine(_dir, "cover.jpg");
        WritePng(path, 400, 400);

        var dialog = new ArtworkViewerDialog(path) { Width = 1000, Height = 800 };
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        PumpUntil(() => closed);
        Assert.True(closed);
    }
}
