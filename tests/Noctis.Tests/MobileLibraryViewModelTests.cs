using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>The phone Library page: counts, song list, add-folder → settings + scan, rescan.</summary>
public class MobileLibraryViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class ScriptedPicker : IFolderPicker
    {
        public string? Next;
        public Task<string?> PickFolderAsync() => Task.FromResult(Next);
    }

    private static Track T(string title, bool fav = false, int daysAgo = 0) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = "A", Album = "B", FilePath = "content://x/" + title,
        IsFavorite = fav, DateAdded = DateTime.UtcNow.AddDays(-daysAgo)
    };

    [Fact]
    public async Task Initialize_ReadsFoldersFromSettings_AndCountsFromTheLibrary()
    {
        var persistence = new PersistenceService(_root);
        var settings = await persistence.LoadSettingsAsync();
        settings.MusicFolders.Add("content://tree/music");
        await persistence.SaveSettingsAsync(settings);

        var library = new FakeLibraryService();
        library.TrackList.AddRange(new[] { T("b"), T("a", fav: true), T("c", daysAgo: 60) });
        var vm = new LibraryViewModel(library, persistence, new ScriptedPicker(), marshal: a => a());

        await vm.InitializeAsync();

        Assert.True(vm.HasFolders);
        Assert.Equal(new[] { "content://tree/music" }, vm.Folders);
        Assert.Equal(3, vm.SongCount);
        Assert.Equal(1, vm.FavoriteCount);
        Assert.Equal(2, vm.RecentlyAddedCount);           // 30-day window excludes the 60-day-old track
        Assert.Equal(new[] { "a", "b", "c" }, vm.Songs.Select(t => t.Title)); // sorted by title
    }

    [Fact]
    public async Task AddFolder_PersistsThePickedUri_AndScansIt()
    {
        var persistence = new PersistenceService(_root);
        var library = new FakeLibraryService();
        var picker = new ScriptedPicker { Next = "content://tree/picked" };
        var vm = new LibraryViewModel(library, persistence, picker, marshal: a => a());
        await vm.InitializeAsync();
        Assert.False(vm.HasFolders);

        await vm.AddFolderCommand.ExecuteAsync(null);

        var saved = await persistence.LoadSettingsAsync();
        Assert.Equal(new[] { "content://tree/picked" }, saved.MusicFolders);
        Assert.True(vm.HasFolders);
        Assert.Equal(new[] { "content://tree/picked" }, library.ScannedFolders);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task AddFolder_Cancelled_ChangesNothing()
    {
        var persistence = new PersistenceService(_root);
        var library = new FakeLibraryService();
        var vm = new LibraryViewModel(library, persistence, new ScriptedPicker { Next = null }, marshal: a => a());
        await vm.InitializeAsync();

        await vm.AddFolderCommand.ExecuteAsync(null);

        Assert.Empty((await persistence.LoadSettingsAsync()).MusicFolders);
        Assert.Empty(library.ScannedFolders);
    }

    [Fact]
    public async Task LibraryUpdated_RefreshesCountsAndSongs()
    {
        var library = new FakeLibraryService();
        var vm = new LibraryViewModel(library, new PersistenceService(_root), new ScriptedPicker(), marshal: a => a());
        await vm.InitializeAsync();
        Assert.Equal(0, vm.SongCount);

        library.TrackList.Add(T("new"));
        library.RaiseLibraryUpdated();

        Assert.Equal(1, vm.SongCount);
        Assert.Single(vm.Songs);
    }
    /// <summary>Settings that cannot be written (disk full, storage gone): the save throws.</summary>
    private sealed class UnsavablePersistence : TestPersistenceService
    {
        public override Task SaveSettingsAsync(AppSettings settings) => throw new IOException("disk full");
    }

    [Fact]
    public async Task SetAlbumPinned_SettingsSaveFails_IsLoggedNotThrown()
    {
        using var persistence = new UnsavablePersistence();
        var vm = new LibraryViewModel(new FakeLibraryService(), persistence, new ScriptedPicker(), marshal: a => a());
        await vm.InitializeAsync();

        // An exception escaping here reaches the UI thread from the sheet's async handler and
        // kills the process.
        await vm.SetAlbumPinnedAsync(Guid.NewGuid(), pinned: true);
    }

    [Fact]
    public async Task AddFolder_SettingsSaveFails_IsLoggedNotThrown()
    {
        using var persistence = new UnsavablePersistence();
        var library = new FakeLibraryService();
        var vm = new LibraryViewModel(library, persistence, new ScriptedPicker { Next = "content://tree/picked" }, marshal: a => a());
        await vm.InitializeAsync();

        await vm.AddFolderCommand.ExecuteAsync(null);

        Assert.False(vm.IsScanning);
    }
}
