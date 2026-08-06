using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Regression tests for dropped files silently failing to import.
/// <para>
/// The drag-and-drop import registers a managed import root when the machine has no
/// usable music folder yet, then copies the dropped files there and imports them itself.
/// <see cref="SettingsViewModel.AddFolderPath"/> used to always kick off a fire-and-forget
/// library scan, which enumerated the brand-new (still empty) root and then published its
/// own authoritative track list — overwriting the tracks the import had just added. The
/// drop vanished on a fresh install while working fine on any machine that already had a
/// configured folder, because that path never registers a new root at all.
/// </para>
/// </summary>
public class DropImportFolderScanTests
{
    private static (SettingsViewModel Vm, ScanCountingLibraryService Library) MakeViewModel()
    {
        var library = new ScanCountingLibraryService();
        var vm = new SettingsViewModel(new TestPersistenceService(), library, new NoOpPlayHistoryService());
        return (vm, library);
    }

    [Fact]
    public async Task AddFolderPath_WithoutAutoScan_DoesNotStartAScan()
    {
        var (vm, library) = MakeViewModel();
        var dir = Directory.CreateTempSubdirectory("noctis-drop-test-");
        try
        {
            await vm.AddFolderPath(dir.FullName, autoScan: false);

            Assert.Contains(dir.FullName, vm.MusicFolders);
            Assert.Equal(0, library.ScanCount);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public async Task AddFolderPath_DefaultsToScanning()
    {
        // The Settings folder picker still relies on the automatic scan.
        var (vm, library) = MakeViewModel();
        var dir = Directory.CreateTempSubdirectory("noctis-drop-test-");
        try
        {
            await vm.AddFolderPath(dir.FullName);
            await library.WaitForScanAsync();

            Assert.Equal(1, library.ScanCount);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class ScanCountingLibraryService : ILibraryService
    {
        private readonly TaskCompletionSource _scanned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ScanCount { get; private set; }

        /// <summary>The scan is started fire-and-forget, so wait for it rather than sleeping.</summary>
        public Task WaitForScanAsync() => _scanned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task ScanAsync(IEnumerable<string> folders, CancellationToken ct = default)
        {
            ScanCount++;
            _scanned.TrySetResult();
            return Task.CompletedTask;
        }

        public IReadOnlyList<Track> Tracks => Array.Empty<Track>();
        public IReadOnlyList<Album> Albums => Array.Empty<Album>();
        public IReadOnlyList<Artist> Artists => Array.Empty<Artist>();
        public event EventHandler? LibraryUpdated { add { } remove { } }
        public event EventHandler<int>? ScanProgress { add { } remove { } }
        public event EventHandler? FavoritesChanged { add { } remove { } }
        public event EventHandler<List<string>>? MusicFoldersChanged { add { } remove { } }
        public event EventHandler<string[]>? ScanAborted { add { } remove { } }
        public Task PauseActiveScanForShutdownAsync(TimeSpan timeout) => Task.CompletedTask;
        public Task ImportFilesAsync(IEnumerable<string> filePaths, CancellationToken ct = default, IProgress<int>? progress = null) => Task.CompletedTask;
        public Track? GetTrackById(Guid id) => null;
        public Album? GetAlbumById(Guid id) => null;
        public IReadOnlyList<Album> GetAlbumsByArtist(string artistName) => Array.Empty<Album>();
        public Task RemoveTrackAsync(Guid id) => Task.CompletedTask;
        public Task RemoveTracksAsync(IEnumerable<Guid> ids) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<Guid, Guid>> RelocateTracksAsync(
            IReadOnlyList<(string oldPath, string newPath)> moves, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(new Dictionary<Guid, Guid>());
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
        public Task SaveTrackUserStateAsync(IReadOnlyCollection<Track> tracks) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
        public Task RebuildIndexAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void NotifyFavoritesChanged() { }
        public void NotifyFavoritesChanged(IReadOnlyCollection<Track>? changed) { }
        public Task SetTracksRatingAsync(IReadOnlyList<Track> tracks, int rating) => Task.CompletedTask;
        public Task SetTracksDislikedAsync(IReadOnlyList<Track> tracks, bool isDisliked) => Task.CompletedTask;
        public Task SetTracksSnoozedAsync(IReadOnlyList<Track> tracks, DateTime? until) => Task.CompletedTask;
        public void NotifyMetadataChanged() { }
        public Task<int> ApplyMergeFeaturedFromTitlesAsync(bool enabled, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> BackfillMissingArtworkAsync(CancellationToken ct = default) => Task.FromResult(0);
    }
}
