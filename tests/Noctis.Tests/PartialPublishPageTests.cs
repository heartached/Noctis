using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A scan's progressive fill publishes only the tracks found SO FAR every 1.5 s
/// (<see cref="ILibraryService.IsPublishingPartial"/>). An album or folder missing from
/// such a publish is "not walked yet", not removed: the album page must not close itself
/// and the Folders tab must not drop the user's folder mid-scan. The authoritative
/// publish (flag false) still reconciles real removals.
/// </summary>
public class PartialPublishPageTests
{
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
        public Task<string?> GetAlbumDescriptionAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<string?> GetAlbumDescriptionFullAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task SetAlbumDescriptionOverrideAsync(string artistName, string albumName, string? description, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ClearAlbumDescriptionOverrideAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>TestPersistenceService with configurable settings (re-implements the
    /// interface so the Folders view model sees these music folders).</summary>
    private sealed class SettingsPersistence : TestPersistenceService, IPersistenceService
    {
        public AppSettings Settings { get; } = new();
        public new Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);
    }

    [AvaloniaFact]
    public void AlbumPage_PartialPublishWithoutTheAlbum_StaysOpen()
    {
        var lib = new FakeLibraryService(); // Albums is empty: the album is absent
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var track = new Track { Id = Guid.NewGuid(), Title = "T", FilePath = TestPaths.Primary("Music", "A", "t.flac") };
        var album = new Album { Id = Guid.NewGuid(), Name = "A", Artist = "B", Tracks = new List<Track> { track } };
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm());
        var backRequests = 0;
        vm.BackRequested += (_, _) => backRequests++;

        // The scan hasn't reached this album's folder yet.
        lib.IsPublishingPartial = true;
        lib.RaiseLibraryUpdated();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, backRequests);
        Assert.Single(vm.Tracks);

        // The authoritative publish: the album really is gone, so the page still closes.
        lib.IsPublishingPartial = false;
        lib.RaiseLibraryUpdated();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, backRequests);
    }

    private static Task RefreshAsync(LibraryFoldersViewModel vm)
    {
        vm.MarkDirty();
        var method = typeof(LibraryFoldersViewModel).GetMethod("RefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(vm, null)!;
    }

    [AvaloniaFact]
    public async Task Folders_PartialPublishWithoutTheSelectedFolder_KeepsTheUserInIt()
    {
        var root = TestPaths.Primary("Music");
        var inA = new Track { Id = Guid.NewGuid(), Title = "A1", FilePath = Path.Combine(root, "A", "a1.flac") };
        var inB = new Track { Id = Guid.NewGuid(), Title = "B1", FilePath = Path.Combine(root, "B", "b1.flac") };

        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(new[] { inA, inB });
        var persistence = new SettingsPersistence();
        persistence.Settings.MusicFolders.Add(root);
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryFoldersViewModel(lib, player, persistence, new SidebarViewModel(persistence, lib));

        await RefreshAsync(vm);
        var folderB = vm.RootNodes.Single().Children.Single(n => n.DisplayName == "B");
        vm.SelectedNode = folderB;
        Assert.Equal(new[] { "B1" }, vm.SelectedFolderTracks.Select(t => t.Title));

        // Progressive fill: only folder A has been walked so far.
        lib.TrackList.Remove(inB);
        lib.IsPublishingPartial = true;
        await RefreshAsync(vm);

        Assert.Equal(folderB.FullPath, vm.SelectedNode?.FullPath);
        Assert.Equal(new[] { "B1" }, vm.SelectedFolderTracks.Select(t => t.Title));

        // The authoritative publish re-selects the folder in the rebuilt tree.
        lib.TrackList.Add(inB);
        lib.IsPublishingPartial = false;
        await RefreshAsync(vm);

        Assert.Equal(folderB.FullPath, vm.SelectedNode?.FullPath);
        Assert.Equal(new[] { "B1" }, vm.SelectedFolderTracks.Select(t => t.Title));

        // A folder that is really gone (authoritative publish) is still deselected.
        lib.TrackList.Remove(inB);
        await RefreshAsync(vm);

        Assert.Null(vm.SelectedNode);
        Assert.Empty(vm.SelectedFolderTracks);
    }
}
