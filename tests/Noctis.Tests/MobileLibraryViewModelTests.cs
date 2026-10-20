using System;
using System.Collections.Generic;
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
        public List<string> Released { get; } = new();
        public Task<string?> PickFolderAsync() => Task.FromResult(Next);
        public void ReleaseAccess(string folder) => Released.Add(folder);
    }

    private const string Tree = "content://com.android.externalstorage.documents/tree/";
    private const string Music = Tree + "primary%3AMusic";
    private const string Tones = Tree + "primary%3AMusic%2FTones";
    private const string Beats = Tree + "primary%3AMusic%2FBeats";

    private async Task<(LibraryViewModel Vm, FakeLibraryService Library, ScriptedPicker Picker, PersistenceService Persistence)> WithFolders(params string[] folders)
    {
        var persistence = new PersistenceService(_root);
        var settings = await persistence.LoadSettingsAsync();
        settings.MusicFolders.AddRange(folders);
        await persistence.SaveSettingsAsync(settings);
        var library = new FakeLibraryService();
        var picker = new ScriptedPicker();
        var vm = new LibraryViewModel(library, persistence, picker, marshal: a => a());
        await vm.InitializeAsync();
        return (vm, library, picker, persistence);
    }

    [Theory]
    [InlineData(Tones, Music, true)]
    [InlineData(Music, Tones, false)]
    [InlineData(Tones, Tones, false)]                                       // the same folder is not inside itself
    [InlineData(Tree + "primary%3AMusicVideos", Music, false)]              // a name that merely starts the same
    [InlineData(Tree + "1234-ABCD%3AMusic", Tree + "1234-ABCD%3A", true)]   // a whole SD card
    [InlineData(Tree + "1234-ABCD%3AMusic%2FTones", Music, false)]          // another volume
    [InlineData("content://other.provider/tree/primary%3AMusic%2FTones", Music, false)]
    public void IsInside_ComparesTheFoldersNotTheirText(string folder, string root, bool inside) =>
        Assert.Equal(inside, LibraryViewModel.IsInside(folder, root));

    /// <summary>Device 10-13: Music/NoctisTest, then Music, listed every NoctisTest song twice
    /// ("Flac Tone" ×2): a SAF song's path carries the folder it was found through. The desktop
    /// refuses a folder inside (or around) a listed one; so does the phone now, and gives back
    /// the access the pick just granted.</summary>
    /// <summary>Add Folder awaits the scan it starts (minutes over SAF), and an async command is
    /// greyed out while it runs: Add Folder (and the Reconnect card, the same command) stayed
    /// dead for the whole scan, though a folder added mid-scan is handled (a second pass).</summary>
    [Fact]
    public async Task AddFolder_StaysTappable_WhileTheScanItStartedRuns()
    {
        var (vm, library, picker, _) = await WithFolders(Music);
        var hold = new TaskCompletionSource();
        library.HoldScan = hold;
        picker.Next = Tree + "1234-ABCD%3AMusic";
        var first = vm.AddFolderCommand.ExecuteAsync(null);
        for (var waited = 0; library.ScanPasses.Count == 0 && waited < 5000; waited += 10) await Task.Delay(10);

        Assert.True(vm.AddFolderCommand.CanExecute(null));

        hold.SetResult();
        await first;
    }

    /// <summary>At a cold start the folders (so Rescan and Add Folder) are on screen before the
    /// library has loaded. A scan then would start from an empty library while the load fills
    /// it, re-reading every song as new (no favourites, plays or Date Added) and racing the load
    /// to publish. It waits for the load instead, then runs.</summary>
    [Fact]
    public async Task Rescan_BeforeTheLibraryLoaded_WaitsForTheLoad()
    {
        var persistence = new PersistenceService(_root);
        var settings = await persistence.LoadSettingsAsync();
        settings.MusicFolders.Add(Music);
        await persistence.SaveSettingsAsync(settings);
        var library = new FakeLibraryService();
        var vm = new LibraryViewModel(library, persistence, new ScriptedPicker(), marshal: a => a());
        vm.ApplySettings(settings);                                            // what the host does first

        await vm.RescanCommand.ExecuteAsync(null);
        Assert.Empty(library.ScanPasses);

        await vm.InitializeAsync();
        for (var waited = 0; library.ScanPasses.Count == 0 && waited < 5000; waited += 10) await Task.Delay(10);
        Assert.Equal(new[] { Music }, Assert.Single(library.ScanPasses));
    }

    [Fact]
    public async Task AddFolder_InsideAListedFolder_IsRefusedWithANote()
    {
        var (vm, library, picker, persistence) = await WithFolders(Music);
        picker.Next = Tones;

        await vm.AddFolderCommand.ExecuteAsync(null);

        Assert.Equal(new[] { Music }, (await persistence.LoadSettingsAsync()).MusicFolders);
        Assert.Equal(new[] { Music }, vm.Folders);
        Assert.Empty(library.ScanPasses);
        Assert.Equal("Already covered by “Music”.", vm.StatusText);
        Assert.Equal(new[] { Tones }, picker.Released);
    }

    [Fact]
    public async Task AddFolder_AroundAListedFolder_IsRefusedWithANote()
    {
        var (vm, library, picker, persistence) = await WithFolders(Tones);
        picker.Next = Music;

        await vm.AddFolderCommand.ExecuteAsync(null);

        Assert.Equal(new[] { Tones }, (await persistence.LoadSettingsAsync()).MusicFolders);
        Assert.Empty(library.ScanPasses);
        Assert.Equal("“Tones” is inside that folder. Remove it first.", vm.StatusText);
        Assert.Equal(new[] { Music }, picker.Released);
    }

    /// <summary>A folder whose access was lost is picked again: listed once, its grant kept, and
    /// the library rescanned so its songs play again.</summary>
    [Fact]
    public async Task AddFolder_TheSameFolderAgain_KeepsOneEntry_AndRescans()
    {
        var (vm, library, picker, persistence) = await WithFolders(Tones, Beats);
        picker.Next = Beats;

        await vm.AddFolderCommand.ExecuteAsync(null);

        Assert.Equal(new[] { Tones, Beats }, (await persistence.LoadSettingsAsync()).MusicFolders);
        Assert.Equal(new[] { Tones, Beats }, library.ScanPasses.Single());
        Assert.Empty(picker.Released);
    }

    /// <summary>Desktop parity (SettingsViewModel.RemoveFolder): the folder leaves the settings,
    /// the phone gives up its access, and a rescan of the rest takes its songs out.</summary>
    [Fact]
    public async Task RemoveFolder_LeavesTheSettings_GivesUpAccess_AndRescansTheRest()
    {
        var (vm, library, picker, persistence) = await WithFolders(Tones, Beats);

        await vm.RemoveFolderAsync(Tones);

        Assert.Equal(new[] { Beats }, (await persistence.LoadSettingsAsync()).MusicFolders);
        Assert.Equal(new[] { Beats }, vm.Folders);
        Assert.Equal(new[] { Tones }, picker.Released);
        Assert.Equal(new[] { Beats }, library.ScanPasses.Single());
        Assert.False(vm.IsScanning);
    }

    /// <summary>The last folder removed: the scan still runs, with no folders, so its songs go
    /// (a plain Rescan with none does nothing).</summary>
    [Fact]
    public async Task RemoveFolder_TheLastOne_ScansWithNoFolders()
    {
        var (vm, library, _, _) = await WithFolders(Tones);

        await vm.RemoveFolderAsync(Tones);
        Assert.Empty(library.ScanPasses.Single());
        Assert.False(vm.HasFolders);

        await vm.RescanCommand.ExecuteAsync(null);
        Assert.Single(library.ScanPasses);
    }

    /// <summary>Desktop parity (Scan on Startup): on by default, a launch rescans the folders; off
    /// (saved to settings.json), it does not; with no folder there is nothing to scan.</summary>
    [Fact]
    public async Task ScanOnStartup_ScansAtLaunchWhenOn_AndIsSaved()
    {
        var (vm, library, _, persistence) = await WithFolders(Tones);
        Assert.True(vm.ScanOnStartup);

        await vm.ScanAtStartupAsync();
        Assert.Equal(new[] { Tones }, library.ScanPasses.Single());

        vm.ScanOnStartup = false;
        for (var waited = 0; (await persistence.LoadSettingsAsync()).ScanOnStartup && waited < 5000; waited += 10) await Task.Delay(10);
        Assert.False((await persistence.LoadSettingsAsync()).ScanOnStartup);
        await vm.ScanAtStartupAsync();
        Assert.Single(library.ScanPasses);

        var emptyLibrary = new FakeLibraryService();
        var empty = new LibraryViewModel(emptyLibrary, new PersistenceService(Path.Combine(_root, "empty")), new ScriptedPicker(), marshal: a => a());
        await empty.InitializeAsync();
        Assert.True(empty.ScanOnStartup);
        await empty.ScanAtStartupAsync();
        Assert.Empty(emptyLibrary.ScanPasses);
    }

    /// <summary>A folder added or removed while a scan runs: the scan goes again with the folders
    /// as they are now (it used to return at once, and the new folder's songs never came).</summary>
    [Fact]
    public async Task FoldersChangedDuringAScan_RunAnotherPass()
    {
        var (vm, library, picker, _) = await WithFolders(Tones, Beats);
        var hold = new TaskCompletionSource();
        library.HoldScan = hold;
        var rescan = vm.RescanCommand.ExecuteAsync(null);
        Assert.True(vm.IsScanning);
        // The first pass is under way (it has read the folders) before they change.
        for (var waited = 0; library.ScanPasses.Count == 0 && waited < 5000; waited += 10) await Task.Delay(10);
        Assert.Single(library.ScanPasses);

        await vm.RemoveFolderAsync(Tones);
        picker.Next = Music + "%2FNew";
        await vm.AddFolderCommand.ExecuteAsync(null);
        hold.SetResult();
        await rescan;

        Assert.Equal(2, library.ScanPasses.Count);
        Assert.Equal(new[] { Beats, Music + "%2FNew" }, library.ScanPasses[1]);
        Assert.False(vm.IsScanning);
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

    /// <summary>Cold launch: the host hands over the settings before the library loads, and the
    /// Library rows show from them at once; the sections below wait for the library.</summary>
    [Fact]
    public async Task ApplySettings_ShowsTheRows_BeforeTheLibraryLoads_AndTheSectionsAfter()
    {
        var persistence = new PersistenceService(_root);
        var settings = await persistence.LoadSettingsAsync();
        settings.MusicFolders.Add("content://tree/music");
        settings.PhoneLibraryRows.AddRange(new[] { "Songs", "Albums" });
        await persistence.SaveSettingsAsync(settings);
        var vm = new LibraryViewModel(new FakeLibraryService(), persistence, new ScriptedPicker(), marshal: a => a());

        vm.ApplySettings(settings);

        Assert.True(vm.HasFolders);
        Assert.Equal(new[] { "Songs", "Albums" }, vm.LibraryRowKeys);
        Assert.False(vm.ShowConnectCard);
        Assert.False(vm.ShowSections);       // no pin hint over a Pinned rail that has not loaded

        await vm.InitializeAsync();
        Assert.True(vm.ShowSections);
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
