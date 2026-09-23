using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
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

    [Fact]
    public void RegionPixels_CoverTheShapeInDevicePixels()
    {
        var px = MiniPlayerWindow.RegionPixels(new[]
        {
            new MiniPlayerWindow.RegionShape(new Rect(10, 20, 100, 50), 28, false),
            new MiniPlayerWindow.RegionShape(new Rect(0, 0, 120, 120), 60, true),
        }, 1.5);
        // Scaled out to whole pixels; right / bottom +1 because region rects exclude them;
        // the corner is given as a diameter.
        Assert.Equal((15, 30, 166, 106, 84, false), px[0]);
        Assert.Equal((0, 0, 181, 181, 180, true), px[1]);
    }
}
