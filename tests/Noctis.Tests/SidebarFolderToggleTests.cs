using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Regression tests for the Linux crash where collapsing a sidebar playlist folder
/// threw ArgumentOutOfRangeException: selecting a folder-header row rebuilt
/// SidebarRows synchronously inside the ListBox's selection commit, so Avalonia's
/// SelectionModel enumerated stale indices against the shrunken collection.
/// The rebuild must be deferred until the selection operation has completed.
/// </summary>
public class SidebarFolderToggleTests
{
    private static SidebarViewModel MakeViewModel()
    {
        var vm = new SidebarViewModel(new TestPersistenceService(), new StubLibraryService());
        vm.PlaylistItems.Add(new PlaylistNavItem { Key = "p1", Label = "In Folder", Folder = "F1" });
        vm.PlaylistItems.Add(new PlaylistNavItem { Key = "p2", Label = "Loose" });
        vm.RebuildSidebarRows();
        return vm;
    }

    [Fact]
    public void SelectingFolderHeader_DoesNotMutateRowsSynchronously()
    {
        var vm = MakeViewModel();
        var before = vm.SidebarRows.ToList();
        var header = vm.SidebarRows.First(r => r.IsFolder);

        vm.SelectedNavItem = header;

        // The rebuild must not run inside the selection change (it is deferred to
        // the dispatcher); same instances, same order, immediately afterwards.
        Assert.Equal(before, vm.SidebarRows.ToList());
    }

    [Fact]
    public void SelectingFolderHeader_DoesNotNavigate()
    {
        var vm = MakeViewModel();
        string? navigated = null;
        vm.NavigationRequested += (_, key) => navigated = key;

        vm.SelectedNavItem = vm.SidebarRows.First(r => r.IsFolder);

        Assert.Null(navigated);
    }

    [Fact]
    public void ToggleFolderExpansion_CollapsesAndExpands()
    {
        var vm = MakeViewModel();
        Assert.Contains(vm.SidebarRows, r => r.Key == "p1"); // expanded by default

        vm.ToggleFolderExpansion("F1");
        Assert.DoesNotContain(vm.SidebarRows, r => r.Key == "p1");
        Assert.False(vm.SidebarRows.First(r => r.IsFolder).IsExpanded);

        vm.ToggleFolderExpansion("F1");
        Assert.Contains(vm.SidebarRows, r => r.Key == "p1");
        Assert.True(vm.SidebarRows.First(r => r.IsFolder).IsExpanded);
    }

    private sealed class StubLibraryService : ILibraryService
    {
        public IReadOnlyList<Track> Tracks => Array.Empty<Track>();
        public IReadOnlyList<Album> Albums => Array.Empty<Album>();
        public IReadOnlyList<Artist> Artists => Array.Empty<Artist>();
        public event EventHandler? LibraryUpdated { add { } remove { } }
        public event EventHandler<int>? ScanProgress { add { } remove { } }
        public event EventHandler? FavoritesChanged { add { } remove { } }
        public Task ScanAsync(IEnumerable<string> folders, CancellationToken ct = default) => Task.CompletedTask;
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
        public Task ClearAsync() => Task.CompletedTask;
        public Task RebuildIndexAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void NotifyFavoritesChanged() { }
        public Task SetTracksRatingAsync(IReadOnlyList<Track> tracks, int rating) => Task.CompletedTask;
        public Task SetTracksDislikedAsync(IReadOnlyList<Track> tracks, bool isDisliked) => Task.CompletedTask;
        public Task SetTracksSnoozedAsync(IReadOnlyList<Track> tracks, DateTime? until) => Task.CompletedTask;
        public void NotifyMetadataChanged() { }
    }
}
