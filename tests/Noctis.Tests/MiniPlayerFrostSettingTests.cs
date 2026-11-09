using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #76: Settings → Mini Player → "Frosted background" (Windows only, off by
/// default). The toggle is VM-owned, so it must survive SaveAsync's re-base on the file
/// (see SettingsViewModelPersistenceTests), and an open mini player swaps its
/// transparency hint live. What the OS actually paints is not visible headlessly.
/// </summary>
[Collection("MetadataServiceStatics")]
public class MiniPlayerFrostSettingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

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

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private SettingsViewModel CreateSettings() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

    [Fact]
    public void FreshInstall_HasTheFrostOff()
        => Assert.False(new AppSettings().MiniPlayerFrostedBackground);

    [AvaloniaFact]
    public async Task FrostedBackground_SurvivesSaveAndReload()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.False(vm.MiniPlayerFrostedBackground);

        vm.MiniPlayerFrostedBackground = true;
        await vm.SaveAsync();

        // "Restart": a fresh view-model loading from the same data root.
        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.True(reloaded.MiniPlayerFrostedBackground);
        Assert.True(reloaded.GetSettings().MiniPlayerFrostedBackground);
    }

    /// <summary>GitHub #80: the island's mini player button toggle persists.</summary>
    [AvaloniaFact]
    public async Task MiniPlayerButtonToggle_SurvivesSaveAndReload()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.True(vm.PlaybackBarShowMiniPlayer);

        vm.PlaybackBarShowMiniPlayer = false;
        await vm.SaveAsync();

        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.False(reloaded.PlaybackBarShowMiniPlayer);
        Assert.False(reloaded.GetSettings().PlaybackBarShowMiniPlayer);
    }

    [Fact]
    public void TransparencyLevels_AskForABlurOnlyWhenFrosted()
    {
        Assert.Equal(new[] { WindowTransparencyLevel.Transparent }, MiniPlayerWindow.TransparencyLevels(false));
        // Mica is deliberately absent: it tints from the wallpaper, it does not blur
        // what is behind the window. Transparent stays last so no-blur keeps today's look.
        Assert.Equal(
            new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent },
            MiniPlayerWindow.TransparencyLevels(true));
    }

    [Fact]
    public void TransparencyLevels_AreOneSharedArrayPerChoice()
    {
        // Avalonia compares the hint by reference: a new, equal array re-applies it, and
        // macOS then drops the window to Opaque (a dark square behind the card).
        Assert.Same(MiniPlayerWindow.TransparencyLevels(false), MiniPlayerWindow.TransparencyLevels(false));
        Assert.Same(MiniPlayerWindow.TransparencyLevels(true), MiniPlayerWindow.TransparencyLevels(true));
    }

    /// <summary>10-08: the wheel over a mini player design changed the level silently while
    /// muted; adjusting now unmutes, as the playback bar's wheel/drag always did.</summary>
    [AvaloniaFact]
    public void DesignWheelVolume_UnmutesLikeTheBar()
    {
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), new TestPersistenceService(), library);
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoOpPlayHistoryService());
        var vm = new MiniPlayerViewModel(player, lyrics, settings, library);

        player.Volume = 40;
        player.IsMuted = true;
        vm.NudgeVolume(1);
        Assert.False(player.IsMuted);
        Assert.Equal(45, player.Volume);

        vm.NudgeVolume(0); // a fraction that hasn't reached a notch: nothing changes
        Assert.Equal(45, player.Volume);
    }

    [AvaloniaFact]
    public void ReattachingTheViewModel_DoesNotChangeTheHintInstance()
    {
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), new TestPersistenceService(), library);
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoOpPlayHistoryService());
        var vm = new MiniPlayerViewModel(player, lyrics, settings, library);

        var win = new MiniPlayerWindow();
        var changes = 0;
        win.PropertyChanged += (_, e) => { if (e.Property == TopLevel.TransparencyLevelHintProperty) changes++; };
        win.DataContext = vm;   // the constructor already set the hint; this sets it again
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, changes);
    }

    [AvaloniaFact]
    public void TogglingTheSetting_SwapsTheOpenWindowsHintLive()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("SearchIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null)
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml"),
            });

        var library = new FakeLibraryService();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), new TestPersistenceService(), library);
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoOpPlayHistoryService());
        var vm = new MiniPlayerViewModel(player, lyrics, settings, library);

        var win = new MiniPlayerWindow { DataContext = vm };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal(MiniPlayerWindow.TransparencyLevels(false), win.TransparencyLevelHint);

            settings.MiniPlayerFrostedBackground = true;
            Dispatcher.UIThread.RunJobs();
            // The frost is Windows-only; elsewhere the hint must not change.
            Assert.Equal(MiniPlayerWindow.TransparencyLevels(OperatingSystem.IsWindows()), win.TransparencyLevelHint);

            settings.MiniPlayerFrostedBackground = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(MiniPlayerWindow.TransparencyLevels(false), win.TransparencyLevelHint);
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>GitHub #150: the Pill and Sleeve lost the frost on 10-05 (e2340d26), when the
    /// window region on their outline stair-stepped their edges. The region now runs past the
    /// outline (RegionPixels), so every design takes the OS backdrop again, across design
    /// switches; the shadows the region would cut are dropped while frosted.</summary>
    [AvaloniaFact]
    public void Frost_AppliesToEveryDesign_AndDropsTheCutShadows()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("SearchIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null)
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml"),
            });

        var library = new FakeLibraryService();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), new TestPersistenceService(), library);
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoOpPlayHistoryService());
        settings.MiniPlayerFrostedBackground = true;
        var vm = new MiniPlayerViewModel(player, lyrics, settings, library);
        vm.SetDesignCommand.Execute("Pill");

        var win = new MiniPlayerWindow { DataContext = vm };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var frosted = MiniPlayerWindow.TransparencyLevels(OperatingSystem.IsWindows());
            var pillGround = win.FindControl<Border>("PillGround")!;
            var pillCover = win.FindControl<Border>("PillCover")!;
            Assert.Same(frosted, win.TransparencyLevelHint);
            Assert.Equal(OperatingSystem.IsWindows(), pillGround.BoxShadow.Count == 0);
            Assert.Equal(OperatingSystem.IsWindows(), pillCover.BoxShadow.Count == 0);

            vm.SetDesignCommand.Execute("Classic");
            Dispatcher.UIThread.RunJobs();
            Assert.Same(frosted, win.TransparencyLevelHint);

            vm.SetDesignCommand.Execute("Sleeve");
            Dispatcher.UIThread.RunJobs();
            Assert.Same(frosted, win.TransparencyLevelHint);
            Assert.Equal(OperatingSystem.IsWindows(), win.FindControl<Border>("SleeveGround")!.BoxShadow.Count == 0);

            // Frost off: the Sleeve's ground shadow and the Pill's cover shadow come back.
            settings.MiniPlayerFrostedBackground = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(MiniPlayerWindow.TransparencyLevels(false), win.TransparencyLevelHint);
            Assert.Equal(1, win.FindControl<Border>("SleeveGround")!.BoxShadow.Count);
            Assert.Equal(1, pillCover.BoxShadow.Count);
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>The OS backdrop fills the window rect, so while frosted the window is clipped
    /// to the design's outline (a Win32 window region). The region itself is not visible
    /// headlessly; this pins the outline it is built from, per design.</summary>
    [AvaloniaTheory]
    [InlineData("Classic")]
    [InlineData("Pill")]
    [InlineData("Sleeve")]
    public async Task FrostRegion_FollowsEachDesignsOutline(string style)
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("SearchIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null)
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml"),
            });

        var library = new FakeLibraryService();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), new TestPersistenceService(), library);
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoOpPlayHistoryService());
        var vm = new MiniPlayerViewModel(player, lyrics, settings, library);
        vm.SetDesignCommand.Execute(style);

        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
        win.Show();
        try
        {
            var end = Environment.TickCount64 + 400;
            while (Environment.TickCount64 < end)
            {
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(8);
            }

            var shapes = win.FrostRegionShapes();
            switch (style)
            {
                case "Classic":
                    // The glass card fills the window; only its r=28 corners are cut away.
                    var card = Assert.Single(shapes);
                    Assert.Equal(new Rect(win.ClientSize), card.Rect);
                    Assert.Equal(28, card.Radius);
                    Assert.False(card.Ellipse);
                    break;
                case "Pill":
                    // The r=24 slab plus the round cover that hangs off its left end.
                    Assert.Equal(2, shapes.Length);
                    var slab = shapes[0];
                    var cover = shapes[1];
                    Assert.Equal(24, slab.Radius);
                    Assert.True(cover.Ellipse);
                    Assert.Equal(new Size(120, 120), cover.Rect.Size);
                    Assert.True(cover.Rect.Left < slab.Rect.Left, "the cover sticks out past the slab");
                    Assert.True(slab.Rect.Width < win.ClientSize.Width, "the slab is inset from the window rect");
                    break;
                case "Sleeve":
                    var sleeve = Assert.Single(shapes);
                    Assert.Equal(34, sleeve.Radius);
                    Assert.True(sleeve.Rect.Width < win.ClientSize.Width, "the slab is inset from the window rect");
                    break;
            }
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>1v1ctus 2026-10-05: a notch of desktop showed where the Pill's cover met its
    /// slab, top and bottom. The slab's left end (under the cover) is square and its edges sit
    /// just inside the cover's height, so the slab never pokes out past the round cover.</summary>
    [AvaloniaFact]
    public async Task PillSlab_IsSquareUnderTheCover_AndStaysInsideItsHeight()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("SearchIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), new TestPersistenceService(), library);
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoOpPlayHistoryService());
        var vm = new MiniPlayerViewModel(player, lyrics, settings, library);
        vm.SetDesignCommand.Execute("Pill");
        var win = new MiniPlayerWindow { DataContext = vm };
        win.Show();
        try
        {
            var end = Environment.TickCount64 + 400;
            while (Environment.TickCount64 < end)
            {
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(8);
            }
            Rect R(string n) { var c = win.FindControl<Control>(n)!; return new Rect(c.TranslatePoint(new Point(0, 0), win)!.Value, c.Bounds.Size); }
            var ground = win.FindControl<Border>("PillGround")!;
            var slab = R("PillGround");
            var cover = R("PillCover");

            Assert.Equal(0, ground.CornerRadius.TopLeft);
            Assert.Equal(0, ground.CornerRadius.BottomLeft);
            Assert.Equal(24, ground.CornerRadius.TopRight);
            Assert.Equal(cover.Center.X, slab.Left, 1);   // the square end is under the cover
            Assert.Equal(cover.Top + MiniPlayerWindow.PillGroundTrim, slab.Top, 1);
            Assert.Equal(cover.Bottom - MiniPlayerWindow.PillGroundTrim, slab.Bottom, 1);
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>GitHub #125: Settings → "Accent colour on artist and album names" off takes the
    /// mini player's artist lines off the accent too, live, and back on when it is switched on.</summary>
    [AvaloniaFact]
    public void ArtistLines_FollowTheAccentNamesSetting()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("SearchIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        var accent = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Red);
        app.Resources["AccentColorBrush"] = accent;
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), new TestPersistenceService(), library);
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoOpPlayHistoryService());
        settings.AccentTrackLinks = false;
        var vm = new MiniPlayerViewModel(player, lyrics, settings, library);
        var win = new MiniPlayerWindow { DataContext = vm };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var lines = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(win)
                .OfType<Noctis.Controls.MarqueeTextBlock>().Where(m => m.Classes.Contains("mini-artist")).ToList();
            Assert.NotEmpty(lines);
            Assert.True(win.TryFindResource("MiniDrawerTextSoft", out var soft));
            Assert.All(lines, m => Assert.Same(soft, m.Foreground));

            settings.AccentTrackLinks = true;
            Dispatcher.UIThread.RunJobs();
            Assert.All(lines, m => Assert.Same(accent, m.Foreground));
        }
        finally
        {
            win.Close();
        }
    }

    [Fact]
    public void RegionPixels_CoverTheShapeInDevicePixels()
    {
        var px = MiniPlayerWindow.RegionPixels(new[]
        {
            new MiniPlayerWindow.RegionShape(new Rect(10, 20, 100, 50), 28, false),
            new MiniPlayerWindow.RegionShape(new Rect(0, 0, 120, 120), 60, true),
        }, 1.5);
        // Grown by the 2-DIP bleed (radius too, so the corner centres stay put), scaled out
        // to whole pixels; right / bottom +1 because region rects exclude them; the corner is
        // given as a diameter.
        Assert.Equal(2, MiniPlayerWindow.FrostRegionBleed);
        Assert.Equal((12, 27, 169, 109, 90, false), px[0]);
        Assert.Equal((-3, -3, 184, 184, 186, true), px[1]);
    }

    /// <summary>Discord report: at Interface Size 150% the frosted tray menu was cut to its
    /// 100% outline (the region ignored the UiScaleHost's layout transform), hiding the last
    /// rows. The outline must be the card as drawn: scaled size and corner radius.</summary>
    [AvaloniaFact]
    public void TrayMenu_CardShape_FollowsInterfaceSize()
    {
        var card = new MenuFlyoutPresenter { ItemsSource = new[] { "Open Noctis", "Pause", "Quit" } };
        var w = new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowDecorations = WindowDecorations.None,
            Content = new LayoutTransformControl
            {
                LayoutTransform = new ScaleTransform(1.5, 1.5),
                Child = card,
            },
        };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var shape = Assert.IsType<MiniPlayerWindow.RegionShape>(TrayMenuFrost.CardShape(card));
            Assert.Equal(card.Bounds.Width * 1.5, shape.Rect.Width, 3);
            Assert.Equal(card.Bounds.Height * 1.5, shape.Rect.Height, 3);
            Assert.Equal(card.CornerRadius.TopLeft * 1.5, shape.Radius, 3);
            // The scaled card fills the window it sized (layout rounding: within a DIP or so;
            // the unscaled outline stopped a third short).
            Assert.InRange(shape.Rect.Bottom, w.ClientSize.Height - 1.5, w.ClientSize.Height + 1.5);
        }
        finally
        {
            w.Close();
        }
    }

    /// <summary>GitHub #150: the tray icon's right-click menu is Avalonia's own transparent
    /// window (TrayPopupRoot, Name "AvaloniaTrayPopupRoot_…", a MenuFlyoutPresenter as its
    /// content). While Liquid Glass is on it gets the OS blur and a translucent card; off, or
    /// for any other window, nothing changes. This window stands in for Avalonia's private one.</summary>
    [AvaloniaFact]
    public void TrayMenu_IsFrostedOnlyWhileGlassIsOn()
    {
        var glass = true;
        var effects = TrayMenuFrost.TransparencyEffectsOn;
        TrayMenuFrost.TransparencyEffectsOn = () => true; // this machine's setting must not decide
        TrayMenuFrost.Register(() => glass, () => Color.Parse("#202030"));
        Window Open(string name)
        {
            var w = new Window
            {
                Name = name,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowDecorations = WindowDecorations.None,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                Content = new MenuFlyoutPresenter { ItemsSource = new[] { "Open Noctis", "Quit" } },
            };
            w.Show();
            Dispatcher.UIThread.RunJobs();
            return w;
        }

        var tray = Open(TrayMenuFrost.TrayPopupNamePrefix + "Noctis");
        var other = Open("SomeOtherWindow");
        glass = false;
        var trayNoGlass = Open(TrayMenuFrost.TrayPopupNamePrefix + "Noctis");
        // Windows Transparency effects off: DWM draws the blur black, so no frost either.
        glass = true;
        TrayMenuFrost.TransparencyEffectsOn = () => false;
        var trayNoEffects = Open(TrayMenuFrost.TrayPopupNamePrefix + "Noctis");
        try
        {
            var card = (MenuFlyoutPresenter)tray.Content!;
            if (OperatingSystem.IsWindows())
            {
                Assert.Same(MiniPlayerWindow.TransparencyLevels(true), tray.TransparencyLevelHint);
                var fill = Assert.IsAssignableFrom<ISolidColorBrush>(card.Background);
                Assert.Equal(Color.Parse("#202030"), fill.Color);
                Assert.Equal(TrayMenuFrost.CardTintOpacity, fill.Opacity);
                // The region is cut from the card's own layout outline and corner radius.
                var shape = Assert.IsType<MiniPlayerWindow.RegionShape>(TrayMenuFrost.CardShape(card));
                Assert.Equal(card.Bounds.Size, shape.Rect.Size);
                Assert.Equal(card.CornerRadius.TopLeft, shape.Radius);
            }
            Assert.Equal(new[] { WindowTransparencyLevel.Transparent }, other.TransparencyLevelHint);
            Assert.Equal(new[] { WindowTransparencyLevel.Transparent }, trayNoGlass.TransparencyLevelHint);
            Assert.Equal(new[] { WindowTransparencyLevel.Transparent }, trayNoEffects.TransparencyLevelHint);
            Assert.Null(((MenuFlyoutPresenter)trayNoEffects.Content!).Background as ISolidColorBrush is { Opacity: TrayMenuFrost.CardTintOpacity } ? "frosted" : null);
        }
        finally
        {
            glass = false;
            TrayMenuFrost.TransparencyEffectsOn = effects;
            tray.Close();
            other.Close();
            trayNoGlass.Close();
            trayNoEffects.Close();
        }
    }
}
