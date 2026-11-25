using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Advanced › Clear Cache (and Reset) reported on the Library page's scan line,
/// a page away from the button, which cleared itself three seconds later. The result now
/// shows under the buttons on the Advanced page.
/// </summary>
[Collection("ArtworkCache")]
public class SettingsStorageStatusTests : IDisposable
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

    [AvaloniaFact]
    public async Task ClearArtworkCache_ReportsOnTheAdvancedPage_NotTheScanLine()
    {
        var vm = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
        await vm.LoadAsync();
        Directory.CreateDirectory(Path.Combine(_root, "artwork"));
        File.WriteAllText(Path.Combine(_root, "artwork", "cover.jpg"), "x");

        await vm.ClearArtworkCacheCommand.ExecuteAsync(null);

        Assert.Equal(Loc.T("Settings.ArtworkCacheCleared"), vm.StorageStatusText);
        Assert.Equal("", vm.ScanStatusText);
        Assert.False(File.Exists(Path.Combine(_root, "artwork", "cover.jpg")));
    }

    /// <summary>The downscaled copies of the deleted covers (cache/artwork_thumbs) stayed on disk.</summary>
    [AvaloniaFact]
    public async Task ClearArtworkCache_AlsoDeletesTheCoversThumbnails()
    {
        var thumbs = Path.Combine(_root, "cache", "artwork_thumbs");
        ArtworkThumbnailCache.Enable(thumbs);
        try
        {
            File.WriteAllText(Path.Combine(thumbs, "abc-256-v2.thumb"), "x");
            var vm = new SettingsViewModel(new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());
            await vm.LoadAsync();

            await vm.ClearArtworkCacheCommand.ExecuteAsync(null);

            Assert.Empty(Directory.GetFiles(thumbs));
        }
        finally
        {
            ArtworkThumbnailCache.Disable();
        }
    }
}
