using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Appearance › Artwork: the cache usage line under "Find Animated Artwork
/// Automatically" (GitHub #167). It was read once at launch, so loops fetched while the
/// app ran left it at "0 B of 1 GB used" (seen live 11-04).
/// </summary>
public class AnimatedArtworkCacheUsageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-animart-usage-" + Guid.NewGuid().ToString("N"));

    public AnimatedArtworkCacheUsageTests() => Directory.CreateDirectory(_dir);

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [AvaloniaFact]
    public async Task OpeningTheAppearanceTab_RereadsTheCacheFilledSinceLaunch()
    {
        var persistence = new PersistenceService(_dir);
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        vm.AnimatedArtworkAutoFetch = true;
        vm.AnimatedArtworkCacheMb = 1024;
        await vm.RefreshAnimatedArtworkCacheUsageAsync();
        Assert.False(vm.HasAnimatedArtworkCache);

        // An album page fetched a loop while Settings was closed.
        persistence.EnsureAnimatedCoverDir();
        var path = persistence.GetCachedAnimatedCoverPath(Guid.NewGuid());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[256 * 1024 * 1024 / 1024]); // 256 KB

        vm.SelectedSettingsTab = SettingsViewModel.TabGeneral;
        vm.SelectedSettingsTab = SettingsViewModel.TabAppearance;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!vm.HasAnimatedArtworkCache && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }

        Assert.True(vm.HasAnimatedArtworkCache);
        Assert.InRange(vm.AnimatedArtworkCacheUsedPercent, 0.0001, 1);
        Assert.Contains("1 GB", vm.AnimatedArtworkCacheUsage);
        Assert.DoesNotContain("0 B", vm.AnimatedArtworkCacheUsage);
    }
}
