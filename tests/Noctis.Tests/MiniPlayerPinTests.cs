using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Mini player pin (Discord request): pinned, the window drops its minimize box so the
/// shell's Show desktop / Minimize all pass it over, and re-asserts always-on-top after a
/// foreground change. What the shell and a game actually do was measured on Windows 11
/// (see MiniPlayerPin); these pin the pure decisions and the persistence.
/// </summary>
[Collection("MetadataServiceStatics")]
public class MiniPlayerPinTests : IDisposable
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

    private static MiniPlayerViewModel CreateMini(SettingsViewModel settings)
    {
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), new TestPersistenceService(), library);
        return new MiniPlayerViewModel(player, lyrics, settings, library);
    }

    // ── Pure decisions ──

    [Fact]
    public void Pinned_DropsTheMinimizeBox_UnpinnedKeepsTodaysDefault()
    {
        // No minimize box is what makes Show desktop / Minimize all skip the window.
        Assert.False(MiniPlayerPin.CanMinimize(pinned: true));
        Assert.True(MiniPlayerPin.CanMinimize(pinned: false));
    }

    [Theory]
    [InlineData(0x20, 0x10, true, true, true)]    // another window took the foreground
    [InlineData(0x10, 0x10, true, true, false)]   // the mini player itself
    [InlineData(0x20, 0x10, false, true, false)]  // unpinned: today's behaviour
    [InlineData(0x20, 0x10, true, false, false)]  // hidden / closing
    [InlineData(0x00, 0x10, true, true, false)]   // no foreground window
    [InlineData(0x20, 0x00, true, true, false)]   // no native window yet
    public void ShouldReassert_OnlyWhenPinnedShownAndSomeoneElseTookTheForeground(
        long foreground, long self, bool pinned, bool shown, bool expected)
        => Assert.Equal(expected, MiniPlayerPin.ShouldReassert(new IntPtr(foreground), new IntPtr(self), pinned, shown));

    [Fact]
    public void ReassertDelays_AnswerAtOnce_ThenAgainAfterAGameRaisesItself()
    {
        var delays = MiniPlayerPin.ReassertDelays;
        Assert.Equal(TimeSpan.Zero, delays[0]);
        // Measured: a GLFW-style game raises itself ~60ms after it gets focus, so the
        // immediate pass alone loses; a later pass must exist and come after that.
        Assert.Contains(delays, d => d >= TimeSpan.FromMilliseconds(200));
        Assert.Equal(delays.OrderBy(d => d), delays);
    }

    // ── Persistence ──

    [Fact]
    public void FreshInstall_IsUnpinned() => Assert.False(new AppSettings().MiniPlayerPinned);

    [AvaloniaFact]
    public async Task Pin_SurvivesSaveAndReload()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.False(vm.MiniPlayerPinned);

        vm.SetMiniPlayerPinned(true);
        // SaveAsync first re-bases on the file (still unpinned); the pin is window-owned
        // like the placement, so the merge must not pull the stale value back.
        await vm.SaveAsync();

        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.True(reloaded.MiniPlayerPinned);
        Assert.True(reloaded.GetSettings().MiniPlayerPinned);
    }

    [AvaloniaFact]
    public void ViewModel_StartsFromTheStoredPin_AndTogglesItThrough()
    {
        var settings = new SettingsViewModel(new TestPersistenceService(), new FakeLibraryService(), new NoOpPlayHistoryService());
        settings.SetMiniPlayerPinned(true);

        var vm = CreateMini(settings);
        Assert.Equal(MiniPlayerPin.IsSupported, vm.CanPin);
        // Where the pin does nothing it never reports pinned.
        Assert.Equal(MiniPlayerPin.IsSupported, vm.IsPinned);

        vm.TogglePinCommand.Execute(null);
        Assert.False(vm.IsPinned);
        // Written through on Windows; elsewhere the toggle is inert and the stored value untouched.
        Assert.Equal(!MiniPlayerPin.IsSupported, settings.MiniPlayerPinned);

        vm.TogglePinCommand.Execute(null);
        Assert.Equal(MiniPlayerPin.IsSupported, vm.IsPinned);
        Assert.True(settings.MiniPlayerPinned);
    }

    // ── Window ──

    [AvaloniaFact]
    public void Window_DropsTheMinimizeBoxWhilePinned_AndRestoresItOnUnpin()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("SearchIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null)
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml"),
            });

        var settings = new SettingsViewModel(new TestPersistenceService(), new FakeLibraryService(), new NoOpPlayHistoryService());
        var vm = CreateMini(settings);
        var win = new MiniPlayerWindow { DataContext = vm };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            // Unpinned: exactly today's window.
            Assert.True(win.CanMinimize);
            Assert.True(win.Topmost);

            vm.TogglePinCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(!MiniPlayerPin.IsSupported, win.CanMinimize);
            Assert.True(win.Topmost);

            vm.TogglePinCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(win.CanMinimize);
        }
        finally
        {
            win.Close();
        }
    }
}
