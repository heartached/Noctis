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
        vm.ShowArtworkViewer = r => { shown.Add(r.StillPath); return open.Task; };

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
        vm.ShowArtworkViewer = r => { shown.Add(r.StillPath); return Task.CompletedTask; };
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

    // ── Animated cover (Discord: the zoomed cover should animate, with an on/off) ──

    /// <summary>An album page whose folder holds a cover.mp4 (an empty file: File.Exists
    /// passes, there is nothing to decode, so only leases are checked).</summary>
    private async Task<(AlbumDetailViewModel Page, SettingsViewModel Settings, PersistenceService Persistence, string? Loop)> OpenAnimatedPageAsync(
        bool withLoop, bool animatedCovers = true, bool viewerAnimated = true)
    {
        var persistence = new PersistenceService(Path.Combine(_dir, "data"));
        await persistence.SaveSettingsAsync(new AppSettings { EnableAnimatedCovers = animatedCovers, ArtworkViewerAnimated = viewerAnimated });
        var lib = new FakeLibraryService();
        var settings = new SettingsViewModel(persistence, lib, new FakeHistoryLog());
        await settings.LoadAsync();

        var music = Path.Combine(_dir, "music");
        Directory.CreateDirectory(music);
        string? loop = null;
        if (withLoop)
        {
            loop = Path.Combine(music, "cover.mp4");
            File.WriteAllBytes(loop, Array.Empty<byte>());
        }
        var albumId = Guid.NewGuid();
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "T", Album = "A", AlbumArtist = "B", Artist = "B",
            AlbumId = albumId, SourceType = SourceType.Local, FilePath = Path.Combine(music, "01.mp3"),
        };
        var album = new Album { Id = albumId, Name = "A", Artist = "B", Tracks = new List<Track> { track } };
        WritePng(persistence.GetArtworkPath(albumId), 600, 600);
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var page = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm(), settings);
        Dispatcher.UIThread.RunJobs();
        return (page, settings, persistence, loop);
    }

    private static ArtworkViewerRequest Opened(AlbumDetailViewModel page)
    {
        ArtworkViewerRequest? request = null;
        page.ShowArtworkViewer = r => { request = r; return Task.CompletedTask; };
        page.ViewArtworkCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        return request!;
    }

    [AvaloniaFact]
    public async Task Page_WithALoop_OpensTheViewerAnimated()
    {
        var (page, _, _, loop) = await OpenAnimatedPageAsync(withLoop: true);
        Assert.True(page.ShowAnimatedCover);

        var request = Opened(page);
        Assert.Equal(page.HeaderArtPath, request.StillPath);
        Assert.Equal(loop, request.AnimatedPath);
        Assert.True(request.PlayAnimated); // default: animated on
    }

    [AvaloniaFact]
    public async Task Page_AnimatedArtworkOff_OpensTheStillOnly()
    {
        var (page, _, _, _) = await OpenAnimatedPageAsync(withLoop: true, animatedCovers: false);
        Assert.Null(Opened(page).AnimatedPath);
    }

    [AvaloniaFact]
    public async Task Page_NoLoop_OpensTheStillOnly()
    {
        var (page, _, _, _) = await OpenAnimatedPageAsync(withLoop: false);
        Assert.Null(Opened(page).AnimatedPath);
    }

    [AvaloniaFact]
    public async Task Page_ViewerChoice_IsRemembered_AndSaved()
    {
        var (page, settings, persistence, loop) = await OpenAnimatedPageAsync(withLoop: true, viewerAnimated: false);
        var request = Opened(page);
        Assert.Equal(loop, request.AnimatedPath); // still offered: the button can start it
        Assert.False(request.PlayAnimated);

        request.PlayAnimatedChanged!(true);
        Assert.True(settings.ArtworkViewerAnimated);
        Assert.True(Opened(page).PlayAnimated);

        await settings.SaveAsync();
        Assert.True((await persistence.LoadSettingsAsync()).ArtworkViewerAnimated);
    }

    private string EmptyLoop()
    {
        var loop = Path.Combine(_dir, $"loop-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(loop, Array.Empty<byte>());
        return loop;
    }

    private (ArtworkViewerDialog Dialog, List<bool> Saved, string Loop) ShowAnimatedViewer(bool playAnimated = true)
    {
        var still = Path.Combine(_dir, "cover.jpg");
        WritePng(still, 400, 400);
        var loop = EmptyLoop();
        var saved = new List<bool>();
        var dialog = new ArtworkViewerDialog(new ArtworkViewerRequest(still, loop, playAnimated, saved.Add)) { Width = 1000, Height = 800 };
        dialog.Show();
        return (dialog, saved, loop);
    }

    [AvaloniaFact]
    public void Viewer_StillOnly_HasNoAnimatedLayerOrButton()
    {
        var still = Path.Combine(_dir, "cover.jpg");
        WritePng(still, 400, 400);
        var dialog = new ArtworkViewerDialog(new ArtworkViewerRequest(still)) { Width = 1000, Height = 800 };
        dialog.Show();

        Assert.False(dialog.HasAnimatedCover);
        Assert.False(dialog.FindControl<Button>("AnimatedToggle")!.IsVisible);
        Assert.False(dialog.FindControl<Noctis.Controls.AnimatedCoverImage>("AnimatedImage")!.IsDecoding);
        dialog.Close();
    }

    [AvaloniaFact]
    public void Viewer_WithALoop_PlaysIt_AndFadesInOnTheFirstFrame()
    {
        var (dialog, _, loop) = ShowAnimatedViewer();
        var animated = dialog.FindControl<Noctis.Controls.AnimatedCoverImage>("AnimatedImage")!;
        animated.Transitions = null; // read the end value, not the fade
        try
        {
            Assert.True(dialog.FindControl<Button>("AnimatedToggle")!.IsVisible);
            Assert.True(animated.IsDecoding);
            Assert.Equal(1, Noctis.Controls.AnimatedCoverFeed.ConsumerCount(loop));
            Assert.Equal(0, animated.Opacity); // the still shows until a frame is up

            using var frame = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96));
            animated.ShowFrame(frame);
            Assert.True(animated.HasFrame);
            Assert.Equal(1, animated.Opacity);
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void Viewer_Paused_ShowsTheStill_UntilTheButtonStartsIt()
    {
        var (dialog, saved, loop) = ShowAnimatedViewer(playAnimated: false);
        var animated = dialog.FindControl<Noctis.Controls.AnimatedCoverImage>("AnimatedImage")!;
        var toggle = dialog.FindControl<Button>("AnimatedToggle")!;
        try
        {
            Assert.True(toggle.IsVisible);
            Assert.False(animated.IsDecoding);
            Assert.Equal("Play animated cover", Avalonia.Automation.AutomationProperties.GetName(toggle));

            toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True(dialog.IsAnimatedOn);
            Assert.True(animated.IsDecoding);
            Assert.Equal("Pause animated cover", Avalonia.Automation.AutomationProperties.GetName(toggle));
            Assert.Equal(new[] { true }, saved);
        }
        finally
        {
            dialog.Close();
        }
        Assert.Equal(0, Noctis.Controls.AnimatedCoverFeed.ConsumerCount(loop));
    }

    [AvaloniaFact]
    public void Viewer_ButtonClick_PausesAndRemembers_WithoutClosing()
    {
        EnsureAppStyles();
        var (dialog, saved, loop) = ShowAnimatedViewer();
        var animated = dialog.FindControl<Noctis.Controls.AnimatedCoverImage>("AnimatedImage")!;
        var toggle = dialog.FindControl<Button>("AnimatedToggle")!;
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        try
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(350); // past the double-click guard, so a stray overlay click would close
            var centre = toggle.TranslatePoint(new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseDown(centre, MouseButton.Left);
            dialog.MouseUp(centre, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.False(closed);
            Assert.False(dialog.IsAnimatedOn);
            Assert.Equal(new[] { false }, saved);
            PumpUntil(() => !animated.IsDecoding); // fades out over the still, then the lease goes
            Assert.False(animated.IsDecoding);
            Assert.Equal(0, Noctis.Controls.AnimatedCoverFeed.ConsumerCount(loop));
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void Viewer_Close_ReleasesTheAnimatedCover()
    {
        var (dialog, _, loop) = ShowAnimatedViewer();
        var animated = dialog.FindControl<Noctis.Controls.AnimatedCoverImage>("AnimatedImage")!;
        Assert.True(animated.IsDecoding);

        dialog.Close();
        Assert.False(animated.IsDecoding);
        Assert.Null(animated.Source);
        Assert.Equal(0, Noctis.Controls.AnimatedCoverFeed.ConsumerCount(loop));
        Assert.False(Noctis.Controls.AnimatedCoverFeed.HasFeed(loop)); // no lingering decoder
    }

    [AvaloniaFact]
    public void Viewer_WithALoop_EscapeStillCloses_AndReleases()
    {
        var (dialog, _, loop) = ShowAnimatedViewer();
        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        Dispatcher.UIThread.RunJobs();

        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        PumpUntil(() => closed);
        Assert.True(closed);
        Assert.Equal(0, Noctis.Controls.AnimatedCoverFeed.ConsumerCount(loop));
    }
}
